using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using UniversalMediaOS.Core.Archiving;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class TorrentDosDriveAliasTests
{
    [Fact]
    public void SubstDriveRootCannotHideAReservedPhysicalParent()
    {
        using var fixture = new OwnedSubstFolder();
        using (var owner = new TorrentDownloadOwnership(fixture.Physical, "http://127.0.0.1:9201")) owner.Reserve();
        string marker = Path.Combine(fixture.Physical, ".umos-torrent-owner.json");
        byte[] originalMarker = File.ReadAllBytes(marker);
        string alias = fixture.Map(fixture.Child);

        var error = Assert.Throws<TorrentDownloadOwnershipException>(() =>
        {
            using var unexpected = new TorrentDownloadOwnership(alias, "http://127.0.0.1:9202");
        });

        Assert.Contains("overlapping", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalMarker, File.ReadAllBytes(marker));
        Assert.False(File.Exists(Path.Combine(fixture.Child, ".umos-torrent-owner.json")));
    }

    [Fact]
    public void SubstDriveRootReusesItsExistingPhysicalReservation()
    {
        using var fixture = new OwnedSubstFolder();
        using (var owner = new TorrentDownloadOwnership(fixture.Physical, "http://127.0.0.1:9201")) owner.Reserve();
        string marker = Path.Combine(fixture.Physical, ".umos-torrent-owner.json");
        byte[] originalMarker = File.ReadAllBytes(marker);
        string alias = fixture.Map(fixture.Physical);

        using (var owner = new TorrentDownloadOwnership(alias, "http://127.0.0.1:9201"))
        {
            Assert.True(owner.WasReserved);
            Assert.True(owner.IsReserved);
            Assert.Equal(fixture.Physical, TorrentDownloadOwnership.NormalizePath(alias), ignoreCase: true);
            Assert.True(TorrentDownloadOwnership.SamePath(alias, fixture.Physical));
        }
        Assert.Equal(originalMarker, File.ReadAllBytes(marker));
    }

    [Fact]
    public void LongPhysicalDirectoryRetainsReservationAndItsFullIdentity()
    {
        using var fixture = new OwnedSubstFolder();
        string physical = fixture.Physical;
        while (physical.Length <= 280) physical = Path.Combine(physical, "long-download-folder-" + new string('a', 40));
        Directory.CreateDirectory(physical);
        Assert.True(physical.Length > 260);

        using (var owner = new TorrentDownloadOwnership(physical, "http://127.0.0.1:9201")) owner.Reserve();
        string marker = Path.Combine(physical, ".umos-torrent-owner.json");
        byte[] originalMarker = File.ReadAllBytes(marker);
        using (var saved = JsonDocument.Parse(originalMarker))
            Assert.Equal(physical, saved.RootElement.GetProperty("Directory").GetString(), ignoreCase: true);
        using (var reopened = new TorrentDownloadOwnership(physical, "http://127.0.0.1:9201"))
        {
            Assert.True(reopened.WasReserved);
            Assert.Equal(physical, TorrentDownloadOwnership.NormalizePath(physical), ignoreCase: true);
        }
        Assert.Equal(originalMarker, File.ReadAllBytes(marker));
    }

    private sealed class OwnedSubstFolder : IDisposable
    {
        private readonly string _physicalTemp = PhysicalDirectory(Path.GetTempPath());
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes("Retain payload bytes through owned DOS drive alias checks.");
        private string? _drive;
        private string? _target;
        public string Root { get; }
        public string Physical => Path.Combine(Root, "physical");
        public string Child => Path.Combine(Physical, "child");

        public OwnedSubstFolder()
        {
            Root = Path.Combine(_physicalTemp, "UniversalMediaOS-subst-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Child);
            File.WriteAllBytes(Path.Combine(Child, "retained.bin"), _bytes);
        }

        public string Map(string target)
        {
            Assert.Null(_drive);
            AssertOwned(target);
            string physicalTarget = PhysicalDirectory(target);
            Assert.Equal(Path.GetFullPath(target), physicalTarget, ignoreCase: true);
            foreach (char letter in Enumerable.Range('D', 'Z' - 'D' + 1).Reverse().Select(value => (char)value))
            {
                string drive = letter + ":";
                if (Environment.GetLogicalDrives().Any(value => value.StartsWith(drive, StringComparison.OrdinalIgnoreCase)) ||
                    DosTarget(drive) != null) continue;
                var start = new ProcessStartInfo("subst.exe")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add(drive);
                start.ArgumentList.Add(physicalTarget);
                using var process = Process.Start(start)!;
                bool exited = process.WaitForExit(5000);
                if (!exited)
                {
                    process.Kill();
                    Assert.True(process.WaitForExit(5000), "The fixture's own SUBST helper must stop before cleanup.");
                    if (string.Equals(@"\??\" + physicalTarget, DosTarget(drive), StringComparison.OrdinalIgnoreCase))
                    {
                        _drive = drive;
                        _target = DosTarget(drive);
                    }
                    Assert.Fail("The owned SUBST mapping must finish creation.");
                }
                if (process.ExitCode != 0) continue; // SUBST refuses an occupied drive; never replace it.
                _drive = drive;
                _target = @"\??\" + physicalTarget;
                Assert.Equal(_target, DosTarget(drive), ignoreCase: true);
                Assert.Equal(physicalTarget, PhysicalDirectory(drive + Path.DirectorySeparatorChar), ignoreCase: true);
                return drive + Path.DirectorySeparatorChar;
            }
            throw new InvalidOperationException("No unused DOS drive was available for the isolated alias regression.");
        }

        public void Dispose()
        {
            AssertOwned(Child);
            Assert.Equal(Root, PhysicalDirectory(Root), ignoreCase: true);
            Assert.False((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0);
            if (_drive != null)
            {
                Assert.Equal(_target, DosTarget(_drive));
                // Exact target matching removes only this fixture's own mapping,
                // even if another mapping appears between verification and removal.
                const uint removeExactRawWithoutBroadcast = 0x2 | 0x4 | 0x1 | 0x8;
                if (!DefineDosDevice(removeExactRawWithoutBroadcast, _drive, _target))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The fixture's exact SUBST mapping could not be released.");
            }
            Assert.Equal(_bytes, File.ReadAllBytes(Path.Combine(Child, "retained.bin")));
            AssertOwned(Child);
            Assert.Equal(Root, PhysicalDirectory(Root), ignoreCase: true);
            Directory.Delete(Root, recursive: true);
        }

        private void AssertOwned(string path)
        {
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(_physicalTemp) + Path.DirectorySeparatorChar,
                Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Root + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }

        private static string? DosTarget(string drive)
        {
            var target = new StringBuilder(32768);
            if (QueryDosDevice(drive, target, target.Capacity) != 0) return target.ToString();
            int error = Marshal.GetLastWin32Error();
            if (error == 2) return null;
            throw new Win32Exception(error, "DOS drive availability could not be checked safely.");
        }

        private static string PhysicalDirectory(string path)
        {
            using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var value = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(handle, value, (uint)value.Capacity, 0);
            if (length == 0 || length >= value.Capacity) throw new IOException("The fixture's physical temporary directory could not be verified.");
            string result = value.ToString();
            if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) result = @"\\" + result[8..];
            else if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result[4..];
            return Path.TrimEndingDirectorySeparator(result);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string device, StringBuilder target, int maximum);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DefineDosDevice(uint flags, string device, string? target);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, FileShare sharing,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint maximum, uint flags);
    }
}
