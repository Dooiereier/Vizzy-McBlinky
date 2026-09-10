using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using Jundroo.ModTools;
using ModApi;
using ModApi.Ui;
using UnityEngine;
using UnityEngine.Networking;

namespace Assets.Scripts
{
    /// <summary>
    /// General-purpose "check for a mod update, then show a reminder dialog" system.
    /// Ported from SatelliteTorifune's Volken2 (github.com/SatelliteTorifune/Volken2,
    /// Assets/Scripts/ModUpdater.cs) and translated - original comments were Chinese.
    ///
    /// What it does:
    ///   1. Reads the locally installed version via the game's own mod manager
    ///      (Game.Instance.ModManager.KnownMods, matched by mod name);
    ///   2. Fetches the latest published version over two channels:
    ///        Channel 1: the "tag_name" of a GitHub Releases API response (primary);
    ///        Channel 2: a raw version.txt fallback (used automatically if the API
    ///        channel fails - rate-limited, offline, or no release published yet);
    ///   3. If the published version is newer than the local one, and the player
    ///      hasn't dismissed that specific version before, shows a three-button
    ///      dialog once the main menu is reached (Download / Later / Don't remind me);
    ///   4. "Don't remind me" remembers the dismissed version in PlayerPrefs - it
    ///      only reminds again once an even newer version is published.
    ///
    /// Never blocks the main thread: all network waiting happens inside a coroutine
    /// (UnityWebRequest, polled once per frame) guarded by an overall watchdog timeout -
    /// the main thread stays free the whole time, and a dead/rate-limited/offline
    /// connection gives up after at most 15 seconds rather than hanging indefinitely.
    ///
    /// Checks at most once per game session (a static flag debounces repeat calls).
    /// </summary>
    public class ModUpdater
    {
        // ==================================================================
        // Where the published version comes from.
        // ==================================================================

        // Channel 1: GitHub Releases API - returns JSON, "tag_name" is the latest
        // version (e.g. "0.5" or "v0.6.1" - a leading "v" is stripped automatically).
        // Publish a release with a version-number tag (e.g. "1.1") and this picks it up.
        public const string LatestVersionUrl =
            "https://api.github.com/repos/Dooiereier/Vizzy-McBlinky/releases/latest";

        // Page opened when the player clicks "Download" - the releases list, or a
        // specific release page.
        public const string DownloadUrl =
            "https://github.com/Dooiereier/Vizzy-McBlinky/releases/latest";

        // Channel 2 (fallback): a raw version.txt at the repo root, containing just
        // the version number (e.g. "0.6"). Used automatically if channel 1 fails
        // (rate-limited / offline / no release published yet) - no API rate limit.
        // Points at the main branch, so version.txt needs updating there on release.
        // Leave this empty ("") to disable the fallback channel entirely.
        public const string VersionFileUrl =
            "https://raw.githubusercontent.com/Dooiereier/Vizzy-McBlinky/main/version.txt";

        // The name to look up in Game.Instance.ModManager.KnownMods to find this
        // mod's own installed version - must match ModData.asset's _name exactly.
        private const string ThisModName = "Vizzy McBlinky";

        // Where the player's "don't remind me" choice is stored. Namespaced with the
        // mod's own name so it can't collide with another mod's own update reminder.
        private const string SkippedVersionPrefKey = "VizzyMcBlinky.UpdateReminder.SkippedVersion";

        // Debounce: check at most once per game session (static, shared across instances).
        private static bool _startedThisSession;

        private Version _localVersion;
        private ModUpdaterHost _host;

        /// <summary>
        /// Starts one update check (at most once per game session - repeat calls are
        /// ignored). Call this from OnModInitialized().
        ///
        /// This method itself never makes or waits on any network request - it just
        /// registers a coroutine host and returns immediately. The actual HTTP
        /// request runs asynchronously in a coroutine (UnityWebRequest + per-frame
        /// polling + an overall watchdog); the main thread stays completely free
        /// while waiting for the version number, and gives up after at most 15
        /// seconds even if the network is down.
        /// </summary>
        public void CheckForUpdate()
        {
            try
            {
                if (_startedThisSession) return;
                _startedThisSession = true;

                _localVersion = GetLocalVersion();
                if (_localVersion == null)
                {
                    Debug.Log("[Vizzy McBlinky] Update check skipped - couldn't find this mod's own installed version.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(LatestVersionUrl))
                {
                    // LatestVersionUrl left empty (e.g. temporarily cleared for testing) -
                    // just log, don't show anything.
                    Debug.Log($"[Vizzy McBlinky] Update check - LatestVersionUrl not configured. Current version {_localVersion}.");
                    return;
                }

                // Coroutine host: this class isn't a MonoBehaviour, so a hidden
                // GameObject carries the UnityWebRequest coroutine for it.
                if (_host == null)
                {
                    var go = new GameObject("VizzyMcBlinkyUpdateReminder");
                    GameObject.DontDestroyOnLoad(go);
                    _host = go.AddComponent<ModUpdaterHost>();
                    _host.Owner = this;
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[Vizzy McBlinky] Update check initialization failed: {ex}");
            }
        }

        // Looks up this mod's own installed version through the game's mod manager,
        // rather than a hardcoded constant, so it can't drift out of sync with
        // ModData.asset's own version fields.
        private static Version GetLocalVersion()
        {
            var modInfo = Game.Instance?.ModManager?.KnownMods
                ?.FirstOrDefault(m => m.Name == ThisModName);
            return modInfo?.Version;
        }

        /// <summary>
        /// Coroutine body: fetches the published version over both channels, compares
        /// it to the local version, and - if an update is due - waits for the main
        /// menu and shows the dialog.
        /// </summary>
        public IEnumerator FetchRoutine()
        {
            // Overall watchdog: no matter how slow or broken the network is, the
            // whole "wait for the published version" process must finish before the
            // deadline (Time.realtimeSinceStartup is unaffected by pauses/hitches).
            // UnityWebRequest.timeout only covers a single request - this covers the
            // whole wait, so there's always an upper bound and it can never hang
            // indefinitely (and nothing here blocks the main thread synchronously).
            const float totalTimeoutSeconds = 15f;
            var deadline = Time.realtimeSinceStartup + totalTimeoutSeconds;

            Version latest = null;
            var got = false;

            // Channel 1: GitHub Releases API.
            yield return TryFetchVersion(LatestVersionUrl, deadline, v => { latest = v; got = true; }, () => { });

            // Channel 2: fall back to version.txt if the API failed (rate-limited,
            // offline, or no release published yet) - but only if the overall
            // watchdog hasn't already expired (otherwise just give up, no second request).
            if (!got && Time.realtimeSinceStartup < deadline && !string.IsNullOrWhiteSpace(VersionFileUrl))
            {
                Debug.Log("[Vizzy McBlinky] Update check - API channel unavailable, falling back to version.txt.");
                yield return TryFetchVersion(VersionFileUrl, deadline, v => { latest = v; got = true; }, () => { });
            }

            if (!got || latest == null)
            {
                if (Time.realtimeSinceStartup >= deadline)
                    Debug.Log($"[Vizzy McBlinky] Update check - timed out waiting for a version number (>{totalTimeoutSeconds}s), skipping this session.");
                else
                    Debug.Log("[Vizzy McBlinky] Update check - all channels failed, skipping this session.");
                yield break;
            }

            Debug.Log($"[Vizzy McBlinky] Update check - local version {_localVersion}, published version {latest}.");
            if (latest <= _localVersion) yield break; // already up to date, don't bother the player

            // Has the player already dismissed this specific version?
            if (Version.TryParse(PlayerPrefs.GetString(SkippedVersionPrefKey, ""), out var skipped)
                && latest <= skipped)
            {
                Debug.Log($"[Vizzy McBlinky] Update check - version {latest} was already dismissed by the player.");
                yield break;
            }

            // Wait for the main menu before showing anything - avoids interrupting
            // the player mid-flight or mid-design. Swap InMenuScene for a different
            // check if you'd rather show it somewhere else.
            while (Game.Instance == null || !Game.Instance.SceneManager.InMenuScene)
            {
                yield return null;
            }

            ShowUpdateDialog(latest);
        }

        /// <summary>
        /// Fetches the given URL and parses a version number out of it. Calls
        /// onSuccess(version) on success, onFail() on failure. deadline is the
        /// overall watchdog (Time.realtimeSinceStartup) - past that, the request is
        /// aborted and treated as a failure. Shared download+parse logic for both channels.
        /// </summary>
        private IEnumerator TryFetchVersion(string url, float deadline, Action<Version> onSuccess, Action onFail)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                // GitHub (both the API and raw) requires a non-empty User-Agent, or it returns 403.
                request.SetRequestHeader("User-Agent", "VizzyMcBlinkyModUpdater/1.0");

                // Purely asynchronous waiting here - the main thread stays completely
                // free. Polling isDone once per frame (rather than a plain
                // `yield return request.SendWebRequest()`) lets the watchdog deadline
                // get checked every frame too - past it, the request is aborted, so
                // "wait for the version number" always has an upper bound and always ends.
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (Time.realtimeSinceStartup >= deadline)
                    {
                        request.Abort(); // released along with the request by the using block
                        Debug.Log($"[Vizzy McBlinky] Update check - overall timeout reached, aborted request to {url}.");
                        onFail?.Invoke();
                        yield break;
                    }
                    yield return null; // one cheap boolean check per frame
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[Vizzy McBlinky] Update check - request to {url} failed: {request.error}");
                    onFail?.Invoke();
                    yield break;
                }

                if (TryParseLatestVersion(request.downloadHandler.text, out var version))
                {
                    onSuccess?.Invoke(version);
                }
                else
                {
                    Debug.Log($"[Vizzy McBlinky] Update check - couldn't parse a version from {url}. Raw response: {request.downloadHandler.text}");
                    onFail?.Invoke();
                }
            }
        }

        /// <summary>
        /// Parses a version number out of a site response. Handles plain text
        /// ("0.7" / "v0.7.1"), the GitHub Releases JSON shape ("tag_name":"v0.7.1"),
        /// and {"version":"0.7"}. Generic - no changes needed when reusing this.
        /// </summary>
        private static bool TryParseLatestVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var s = text.Trim();

            // JSON: prefer the "tag_name" (GitHub Releases) or "version" field's value.
            if (s.StartsWith("{") || s.StartsWith("["))
            {
                var match = Regex.Match(s, "\"(?:tag_name|version)\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success) s = match.Groups[1].Value.Trim();
            }

            // Strip a leading v/V, then take the first "number.number..." run
            // (robust against surrounding HTML/whitespace/newlines).
            s = Regex.Replace(s, "^[vV]", "");
            s = Regex.Match(s, @"\d+(?:\.\d+){1,3}").Value;

            return Version.TryParse(s, out version);
        }

        /// <summary>
        /// Shows the three-button dialog: Download / Later / Don't remind me.
        /// </summary>
        private void ShowUpdateDialog(Version latest)
        {
            try
            {
                if (Game.Instance?.UserInterface == null) return;

                var dialog = Game.Instance.UserInterface.CreateMessageDialog(MessageDialogType.ThreeButtons, null, true);
                if (dialog == null) return;

                dialog.MessageText =
                    $"A new version of Vizzy McBlinky is available.\n\n" +
                    $"Latest version: {latest}\n" +
                    $"Current version: {_localVersion}";
                dialog.OkayButtonText = "Download";
                dialog.MiddleButtonText = "Later";
                dialog.CancelButtonText = "Don't remind me";

                // Download.
                dialog.OkayClicked += d =>
                {
                    d.Close();
                    if (!string.IsNullOrEmpty(DownloadUrl))
                        Application.OpenURL(DownloadUrl);
                    else
                        Debug.Log("[Vizzy McBlinky] DownloadUrl isn't configured.");
                };

                // Later: just close, the reminder shows again next session.
                dialog.MiddleClicked += d => d.Close();

                // Don't remind me: remember the dismissed version, no more reminders until a newer one is published.
                dialog.CancelClicked += d =>
                {
                    PlayerPrefs.SetString(SkippedVersionPrefKey, latest.ToString());
                    PlayerPrefs.Save();
                    d.Close();
                };
            }
            catch (Exception ex)
            {
                Debug.Log($"[Vizzy McBlinky] Update reminder dialog failed: {ex}");
            }
        }

        /// <summary>
        /// Coroutine host: gives the non-MonoBehaviour ModUpdater somewhere to run
        /// its UnityWebRequest coroutine. Generic - no changes needed when reusing this.
        /// </summary>
        public class ModUpdaterHost : MonoBehaviour
        {
            public ModUpdater Owner;

            private void Start()
            {
                if (Owner != null)
                {
                    StartCoroutine(Owner.FetchRoutine());
                }
            }

            // Safety net: if the host object is ever destroyed (an unusual scene
            // transition, a reload, etc.), stop the coroutine immediately so no
            // dangling network wait is left running.
            private void OnDestroy()
            {
                StopAllCoroutines();
            }
        }
    }
}
