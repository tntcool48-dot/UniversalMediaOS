# Movie/TV browser replay cookies — October 11, 2026

A bounded diagnostic of original Avatar: The Last Airbender (2005), IMDb tt0417299, S1E2 found a validated native candidate with independently observed matching work/year/unit and one English caption track in 18.781 seconds. The earlier physical lookup returned no accepted native source; this later service result does not establish the earlier failure's cause or qualify restarted TV playback.

The trace exposed a real DrissionPage 4.1.1.4 API error: `ChromiumBase.cookies()` accepts `all_domains` and `all_info`, but the Movie/TV helper passed `as_dict=True`. Its exception fallback silently discarded browser cookies. The helper now uses `page.cookies().as_str()`, preserving only the current page's cookies through the existing replay/header path. Unavailable/empty cookies still produce an empty string. No identity guard, provider selection, deadline or audio claim changed.

The regression uses the installed library's actual CookiesList and a page method with its supported signature. It failed before repair because both replay values were lost. After repair the 19-case native-frame module passes in 0.007 seconds, including empty/error cookies and the existing independent-identity/native-byte/nested-frame safeguards. Five C# paused-clock/resume/error checks passed separately and were not repeated for this Python-only addition.

The first diagnostic wrapper rejected a positional argument. Its receipt is retained as invalid evidence and was excluded from the availability finding. The corrected diagnostic is private: `.artifacts/implementation/package-smoke-20261009/avatar-source-diagnosis-20261011.json`. No raw signed stream or caption bodies are published.

The previous private app closed normally after its pause and accepted-write drain. Updated-package catalog-to-native TV resume and paused-clock verification remain pending. Existing data/configuration and unrelated untracked files are retained.
