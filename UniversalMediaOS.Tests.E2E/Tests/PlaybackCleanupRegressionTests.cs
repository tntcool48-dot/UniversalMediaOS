using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.Core.Data;
using System.Windows.Threading;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class PlaybackCleanupRegressionTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void PlayerClose_DropsCallbacksQueuedBeforeOrAfterDisposal(bool queueBeforeClose)
        {
            RecoveryLayoutTests.RunSta(() =>
            {
                using var player = new PlaybackViewModel(new DatabaseContext());
                var dispatcher = Dispatcher.CurrentDispatcher;
                bool callbackRan = false;
                if (queueBeforeClose)
                    player.RunOnDispatcher(() => callbackRan = true, dispatcher);

                player.Dispose();

                if (!queueBeforeClose)
                    player.RunOnDispatcher(() => callbackRan = true, dispatcher);

                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(player.IsDisposed);
                Assert.False(callbackRan);
            });
        }

        [Fact]
        public void ActivePlayer_StillReceivesDispatchedUpdates()
        {
            RecoveryLayoutTests.RunSta(() =>
            {
                using var player = new PlaybackViewModel(new DatabaseContext());
                var dispatcher = Dispatcher.CurrentDispatcher;
                player.RunOnDispatcher(() => player.PlaybackStatusText = "Updated", dispatcher);
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal("Updated", player.PlaybackStatusText);
            });
        }

        [Fact]
        public void TabClose_DetachesViewBeforeSchedulingNativeDisposal()
        {
            var events = new List<string>();
            Action? deferredDisposal = null;

            MainViewModel.DetachPlaybackThenDispose(
                detachView: () => events.Add("detached"),
                disposePlayback: () => events.Add("disposed"),
                scheduleDisposal: action =>
                {
                    events.Add("scheduled");
                    deferredDisposal = action;
                });

            Assert.Equal(new[] { "detached", "scheduled" }, events);
            Assert.NotNull(deferredDisposal);

            deferredDisposal();

            Assert.Equal(new[] { "detached", "scheduled", "disposed" }, events);
        }

        [Fact]
        public void TabClose_DetachesViewBeforeDisposingOwnedViewModel()
        {
            var events = new List<string>();
            var disposable = new RecordingDisposable(() => events.Add("disposed"));

            MainViewModel.DetachThenDispose(
                detachView: () => events.Add("detached"),
                disposable);

            Assert.Equal(new[] { "detached", "disposed" }, events);
        }

        [Fact]
        public void TabClose_DisposalFailureCannotReattachOrEscapeToTheUiThread()
        {
            int detachCalls = 0;
            var disposable = new RecordingDisposable(() => throw new InvalidOperationException("native teardown failed"));

            Exception? failure = Record.Exception(() =>
                MainViewModel.DetachThenDispose(
                    detachView: () => detachCalls++,
                    disposable));

            Assert.Null(failure);
            Assert.Equal(1, detachCalls);
        }

        [Fact]
        public void DeferredPlaybackDisposal_FailureIsContainedAfterDetachment()
        {
            int detachCalls = 0;
            Action? deferredDisposal = null;

            MainViewModel.DetachPlaybackThenDispose(
                detachView: () => detachCalls++,
                disposePlayback: () => throw new InvalidOperationException("player already released"),
                scheduleDisposal: action => deferredDisposal = action);

            Assert.Equal(1, detachCalls);
            Assert.NotNull(deferredDisposal);
            Assert.Null(Record.Exception(deferredDisposal));
        }

        [Fact]
        public void MediaTab_LifetimeOwnerCanReleaseScopedContent()
        {
            bool disposed = false;
            var lifetime = new RecordingDisposable(() => disposed = true);
            var tab = new MediaTabViewModel(
                "Anime",
                "Anime",
                "icon",
                "accent",
                new TestContentViewModel(),
                lifetime);

            Assert.Same(lifetime, tab.Lifetime);
            tab.Lifetime!.Dispose();
            Assert.True(disposed);
        }

        private sealed class RecordingDisposable : IDisposable
        {
            private readonly Action _onDispose;

            public RecordingDisposable(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                _onDispose();
            }
        }

        private sealed class TestContentViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
        {
        }
    }
}
