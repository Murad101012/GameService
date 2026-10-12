using System;
using System.Collections;
using System.Collections.Generic;
using Audio;
using Ui.ElementReferences;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ui.Controllers
{
    /// <summary>
    /// Social (DeRelay) overlay inside the Menu scene: profile button, auth,
    /// friends, friend requests and account management. Built with UI Toolkit
    /// components like the rest of the Menu.
    /// </summary>
    [RequireComponent(typeof(SocialUiElementReference))]
    [RequireComponent(typeof(UIDocument))]
    public class SocialUiController : MonoBehaviour
    {
        private const string PrefsJwt = "DeRelay.Jwt";
        private const string PrefsRefresh = "DeRelay.RefreshToken";
        private const string PrefsRemember = "DeRelay.RememberMe";
        // Legacy keys from older flows (never written anymore).
        private const string LegacyPrefsToken = "DeRelay.Token";
        private const string LegacyPrefsPassword = "DeRelay.Password";
        private const string LegacyPrefsUsername = "DeRelay.Username";

        // Refresh a bit before the real expiry so slow networks never race it.
        private const long RefreshSkewSeconds = 60;

        private SocialUiElementReference _refs;
        private GlobalAudioPlayer _audio;
        private UIDocument _uiDocument;

        private enum AuthMode { Login, Register, Profile }
        private enum MainTab { Friends, Requests, Account }

        private AuthMode _authMode = AuthMode.Register; // default: Register
        private MainTab _mainTab = MainTab.Friends;
        private bool _requestsReceived = true; // default: You Received
        private int _gender;
        private bool _windowOpen;
        // In-memory session. Populated from PlayerPrefs only when Remember Me
        // was on; otherwise the user must log in again after an app restart.
        private string _jwt = "";
        private string _refreshToken = "";
        private bool _refreshInFlight;

        private void Awake()
        {
            _refs = GetComponent<SocialUiElementReference>();
            _uiDocument = GetComponent<UIDocument>();
            if (_uiDocument == null) _uiDocument = FindFirstObjectByType<UIDocument>();
            _audio = FindFirstObjectByType<GlobalAudioPlayer>();
        }

        private void Start()
        {
            if (_refs == null || !_refs.CheckFinished || !_refs.CheckResult)
            {
                Debug.LogWarning($"{name}: SocialUiElementReference missing/incomplete, disabling SocialUiController.");
                enabled = false;
                return;
            }
            Debug.Log($"{name}: Social UI ready. Profile button "
                + (_refs.ProfileButtonReference != null ? "found." : "MISSING!")); 

            _refs.PasswordReference.isPasswordField = true;
            _refs.ProfileButtonReference.clicked += ToggleWindow;
            _refs.BackToMenuReference.clicked += CloseWindow;

            _refs.ShowLoginReference.clicked += () => SetAuthMode(AuthMode.Login);
            _refs.ShowRegisterReference.clicked += () => SetAuthMode(AuthMode.Register);
            _refs.ProcessReference.clicked += () => StartCoroutine(OnProcess());

            _refs.Gender0Reference.clicked += () => SetGender(0);
            _refs.Gender1Reference.clicked += () => SetGender(1);
            _refs.Gender2Reference.clicked += () => SetGender(2);

            _refs.TabFriendsReference.clicked += () => SetMainTab(MainTab.Friends);
            _refs.TabRequestsReference.clicked += () => SetMainTab(MainTab.Requests);
            _refs.TabAccountReference.clicked += () => SetMainTab(MainTab.Account);

            _refs.ReqReceivedReference.clicked += () => SetRequestsSub(true);
            _refs.ReqSentReference.clicked += () => SetRequestsSub(false);
            _refs.SearchSendReference.clicked += () => StartCoroutine(OnSearchSend());

            _refs.LogoutReference.clicked += OnLogout;
            _refs.DeleteAccountReference.clicked += () => StartCoroutine(OnDeleteAccount());

            SetupSearchPlaceholder();
            SetupBirthFields();
            SetAuthMode(AuthMode.Register);
            SetGender(0);
            // Drop legacy stored credentials (password re-login is gone:
            // the refresh token replaces it) and restore the last session.
            PlayerPrefs.DeleteKey(LegacyPrefsToken);
            PlayerPrefs.DeleteKey(LegacyPrefsPassword);
            PlayerPrefs.DeleteKey(LegacyPrefsUsername);
            PlayerPrefs.Save();
            if (_refs.RememberMeReference != null)
                _refs.RememberMeReference.value = PlayerPrefs.GetInt(PrefsRemember, 1) == 1;
            RestoreSessionFromPrefs();
            // Silent background login so a remembered user never sees the form.
            if (!string.IsNullOrEmpty(_refreshToken))
                StartCoroutine(TrySilentLogin());
            CloseWindow();
        }

        private static string DigitsOnly(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var chars = new char[value.Length];
            int count = 0;
            foreach (char c in value)
            {
                if (c >= '0' && c <= '9') chars[count++] = c;
            }
            return new string(chars, 0, count);
        }

        private void SetupBirthFields()
        {
            _refs.BirthYearReference.RegisterValueChangedCallback(evt =>
            {
                string digits = DigitsOnly(evt.newValue);
                if (digits != evt.newValue) _refs.BirthYearReference.SetValueWithoutNotify(digits);
            });
            _refs.BirthMonthReference.RegisterValueChangedCallback(evt =>
            {
                string digits = DigitsOnly(evt.newValue);
                if (digits != evt.newValue) _refs.BirthMonthReference.SetValueWithoutNotify(digits);
            });
            _refs.BirthDayReference.RegisterValueChangedCallback(evt =>
            {
                string digits = DigitsOnly(evt.newValue);
                if (digits != evt.newValue) _refs.BirthDayReference.SetValueWithoutNotify(digits);
            });
        }

        private bool TryReadBirthDate(out DateTime dateUtc)
        {
            dateUtc = default;
            if (!int.TryParse(_refs.BirthYearReference.value.Trim(), out int year)
                || !int.TryParse(_refs.BirthMonthReference.value.Trim(), out int month)
                || !int.TryParse(_refs.BirthDayReference.value.Trim(), out int day))
            {
                ShowMessage("Enter birth date as numbers (Y / M / D).");
                return false;
            }
            int currentYear = DateTime.UtcNow.Year;
            if (year < 1900 || year > currentYear || month < 1 || month > 12)
            {
                ShowMessage("Birth date is out of range.");
                return false;
            }
            int daysInMonth;
            try
            {
                daysInMonth = DateTime.DaysInMonth(year, month);
            }
            catch
            {
                ShowMessage("Birth date is not a real calendar date.");
                return false;
            }
            if (day < 1 || day > daysInMonth)
            {
                ShowMessage("Birth date is not a real calendar date.");
                return false;
            }
            // UTC Kind: the server stores timestamptz and rejects Unspecified.
            dateUtc = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
            return true;
        }

        // ── Window ──
        private void ToggleWindow()
        {
            Debug.Log($"{name}: Profile button clicked. Opening={_windowOpen}.");
            // Guarantee top stacking even if scene hierarchy order shifts.
            _refs.ProfileButtonReference.BringToFront();
            _refs.SocialLayerReference.BringToFront();
            if (_windowOpen) CloseWindow();
            else OpenWindow();
        }

        private void OpenWindow()
        {
            _windowOpen = true;
            _refs.SocialLayerReference.style.display = DisplayStyle.Flex;
            if (_audio != null) _audio.DimForSocialUI();
            ClearMessage();
            if (!string.IsNullOrEmpty(_jwt))
            {
                // Always re-verify: a valid JWT says nothing about whether
                // the Person profile exists (e.g. registered but never
                // completed it, then restarted the app).
                ShowAuth();
                ShowMessage("Loading…");
                StartCoroutine(AfterLogin());
            }
            else if (!string.IsNullOrEmpty(_refreshToken))
            {
                // Remembered session with a stale JWT: try the background
                // refresh; the window flips to main view if it succeeds.
                ShowAuth();
                ShowMessage("Signing in…");
                StartCoroutine(TrySilentLogin());
            }
            else ShowAuth();
        }

        private void CloseWindow()
        {
            _windowOpen = false;
            if (_refs != null && _refs.SocialLayerReference != null)
                _refs.SocialLayerReference.style.display = DisplayStyle.None;
            if (_audio != null) _audio.RestoreSocialUI();
        }

        // ── Auth view ──
        private void ShowAuth()
        {
            _refs.AuthViewReference.style.display = DisplayStyle.Flex;
            _refs.MainViewReference.style.display = DisplayStyle.None;
            _refs.SocialTitleReference.text = "Profile";
            RefreshAuthFields();
        }

        private void SetAuthMode(AuthMode mode)
        {
            _authMode = mode;
            RefreshAuthFields();
        }

        private void RefreshAuthFields()
        {
            bool isLogin = _authMode == AuthMode.Login;
            bool isProfile = _authMode == AuthMode.Profile;
            SetTabActive(_refs.ShowLoginReference, isLogin);
            SetTabActive(_refs.ShowRegisterReference, _authMode == AuthMode.Register);
            // Login tabs are only for Login/Register. Profile creation reuses
            // the register rows (name/nickname/gender/birth) as its own step.
            if (_refs.AuthTabsReference != null)
                _refs.AuthTabsReference.style.display = isProfile ? DisplayStyle.None : DisplayStyle.Flex;
            // Row 1 (email + password) for Login/Register; profile rows for Profile.
            _refs.AuthRow1Reference.style.display = isProfile ? DisplayStyle.None : DisplayStyle.Flex;
            _refs.AuthRow2Reference.style.display = isProfile ? DisplayStyle.Flex : DisplayStyle.None;
            _refs.AuthRow3Reference.style.display = isProfile ? DisplayStyle.Flex : DisplayStyle.None;
            _refs.BirthRowReference.style.display = isProfile ? DisplayStyle.Flex : DisplayStyle.None;
            _refs.BirthCaptionReference.style.display = isProfile ? DisplayStyle.Flex : DisplayStyle.None;
            _refs.Gender0Reference.style.display = isProfile ? DisplayStyle.Flex : DisplayStyle.None;
            _refs.Gender1Reference.style.display = isProfile ? DisplayStyle.Flex : DisplayStyle.None;
            _refs.Gender2Reference.style.display = isProfile ? DisplayStyle.Flex : DisplayStyle.None;
            var genderRow = _refs.Gender0Reference.parent;
            if (genderRow != null) genderRow.style.display = isProfile ? DisplayStyle.Flex : DisplayStyle.None;
            // Remember Me is a login-only row.
            if (_refs.RememberRowReference != null)
                _refs.RememberRowReference.style.display = isLogin ? DisplayStyle.Flex : DisplayStyle.None;
            if (isProfile && _refs.SocialTitleReference != null)
                _refs.SocialTitleReference.text = "Create Profile";
        }

        private void SetGender(int gender)
        {
            _gender = Mathf.Clamp(gender, 0, 2);
            SetTabActive(_refs.Gender0Reference, _gender == 0);
            SetTabActive(_refs.Gender1Reference, _gender == 1);
            SetTabActive(_refs.Gender2Reference, _gender == 2);
        }

        private static void SetTabActive(Button button, bool active)
        {
            if (button == null) return;
            if (active) button.AddToClassList("social-tab-active");
            else button.RemoveFromClassList("social-tab-active");
        }

        private IEnumerator OnProcess()
        {
            ClearMessage();
            if (_authMode == AuthMode.Login) yield return StartCoroutine(DoLogin());
            else if (_authMode == AuthMode.Register) yield return StartCoroutine(DoRegister());
            else yield return StartCoroutine(DoCompleteProfile());
        }

        private IEnumerator DoRegister()
        {
            // Email + password only: the account stays pending until the user
            // clicks the confirmation link in their inbox.
            string email = _refs.EmailReference.value.Trim();
            string password = _refs.PasswordReference.value;
            var body = new DeRelayApiClient.RegisterRequest { Email = email, Password = password };
            DeRelayApiClient.ApiResult<bool> result = null;
            yield return StartCoroutine(DeRelayApiClient.Register(body, r => result = r));
            if (result == null || !result.Ok)
            {
                ShowMessage(result != null ? result.ErrorMessage : "Register failed.");
                yield break;
            }
            // Success: switch to login and invite the user to confirm + log in.
            SetAuthMode(AuthMode.Login);
            _refs.EmailReference.value = email;
            _refs.PasswordReference.value = "";
            ShowMessage("Account created. Check your email for the confirmation link, then log in.");
        }

        private IEnumerator DoLogin()
        {
            string email = _refs.EmailReference.value.Trim();
            string password = _refs.PasswordReference.value;
            var body = new DeRelayApiClient.LoginRequest { Email = email, Password = password };
            DeRelayApiClient.ApiResult<DeRelayApiClient.TokenPairDto> result = null;
            yield return StartCoroutine(DeRelayApiClient.Login(body, r => result = r));
            if (result == null || !result.Ok)
            {
                // Unconfirmed email lands here too: the server's message
                // (e.g. "email not confirmed") is shown as-is.
                ShowMessage(result != null ? result.ErrorMessage : "Login failed.");
                yield break;
            }
            bool remember = _refs.RememberMeReference != null ? _refs.RememberMeReference.value : true;
            PlayerPrefs.SetInt(PrefsRemember, remember ? 1 : 0);
            StorePair(result.Data, remember);
            PlayerPrefs.Save();
            _refs.PasswordReference.value = "";
            yield return StartCoroutine(AfterLogin());
        }

        /// <summary>
        /// Routes a fresh session: users without a Person profile must create
        /// one before reaching the friends list.
        /// </summary>
        private IEnumerator AfterLogin()
        {
            DeRelayApiClient.ApiResult<bool> check = null;
            yield return StartCoroutine(Authed<bool>(DeRelayApiClient.HasProfile, r => check = r));
            if (check == null || !check.Ok)
            {
                ShowMessage(check != null ? check.ErrorMessage : "Could not check profile.");
                yield break;
            }
            if (check.Data) ShowMain();
            else
            {
                ClearMessage();
                SetAuthMode(AuthMode.Profile);
                ShowAuth();
                ShowMessage("Create your profile to continue.");
            }
        }

        private IEnumerator DoCompleteProfile()
        {
            string first = _refs.FirstNameReference.value.Trim();
            string last = _refs.LastNameReference.value.Trim();
            string nick = _refs.NickNameReference.value.Trim();
            if (!TryReadBirthDate(out DateTime dob)) yield break;
            var body = new DeRelayApiClient.CreateProfileRequest
            {
                FirstName = first,
                LastName = last,
                NickName = nick,
                Gender = _gender,
                // Trailing Z: marks the value UTC so the server timestamptz accepts it.
                DateOfBirth = dob.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
            DeRelayApiClient.ApiResult<int> result = null;
            // Authed: the JWT may have expired while the user filled the form.
            yield return StartCoroutine(Authed<int>(
                (t, d) => DeRelayApiClient.CompleteProfile(body, t, d), r => result = r));
            if (result == null || !result.Ok)
            {
                ShowMessage(result != null ? result.ErrorMessage : "Could not create profile.");
                yield break;
            }
            ShowMain();
        }

        // ── Session: JWT + background refresh ──
        private bool Remembered()
        {
            return PlayerPrefs.GetInt(PrefsRemember, 1) == 1;
        }

        private void RestoreSessionFromPrefs()
        {
            _jwt = "";
            _refreshToken = "";
            if (!Remembered()) return; // session-only: never persisted
            _jwt = PlayerPrefs.GetString(PrefsJwt, "");
            _refreshToken = PlayerPrefs.GetString(PrefsRefresh, "");
        }

        private void StorePair(DeRelayApiClient.TokenPairDto pair, bool persist)
        {
            _jwt = pair != null ? pair.jwtToken ?? "" : "";
            _refreshToken = pair != null ? pair.refreshToken ?? "" : "";
            if (persist)
            {
                PlayerPrefs.SetString(PrefsJwt, _jwt);
                PlayerPrefs.SetString(PrefsRefresh, _refreshToken);
            }
            else
            {
                PlayerPrefs.DeleteKey(PrefsJwt);
                PlayerPrefs.DeleteKey(PrefsRefresh);
            }
        }

        private void ClearSession()
        {
            _jwt = "";
            _refreshToken = "";
            PlayerPrefs.DeleteKey(PrefsJwt);
            PlayerPrefs.DeleteKey(PrefsRefresh);
            PlayerPrefs.Save();
        }

        /// <summary>True when the JWT's "exp" claim is past (or within skew of now).</summary>
        private static bool IsJwtExpiredOrNear(string jwt, long skewSeconds)
        {
            if (string.IsNullOrEmpty(jwt)) return true;
            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length < 2) return true;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                int expIdx = json.IndexOf("\"exp\"", StringComparison.Ordinal);
                if (expIdx < 0) return true;
                int colon = json.IndexOf(':', expIdx);
                int end = json.IndexOfAny(new[] { ',', '}' }, colon + 1);
                if (colon < 0 || end < 0) return true;
                if (!long.TryParse(json.Substring(colon + 1, end - colon - 1).Trim(), out long exp))
                    return true;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return now + skewSeconds >= exp;
            }
            catch
            {
                return true; // unparseable token: treat as expired, refresh path decides
            }
        }

        /// <summary>
        /// Ensures _jwt is usable, refreshing it in the background when needed.
        /// When it returns, _jwt is either fresh or empty (no/dead session).
        /// </summary>
        private IEnumerator EnsureFreshToken()
        {
            if (!IsJwtExpiredOrNear(_jwt, RefreshSkewSeconds)) yield break;
            if (string.IsNullOrEmpty(_refreshToken)) yield break;
            if (_refreshInFlight)
            {
                yield return new WaitUntil(() => !_refreshInFlight);
                yield break;
            }
            _refreshInFlight = true;
            DeRelayApiClient.ApiResult<DeRelayApiClient.TokenPairDto> result = null;
            yield return StartCoroutine(DeRelayApiClient.Refresh(_refreshToken, r => result = r));
            _refreshInFlight = false;
            if (result != null && result.Ok && result.Data != null
                && !string.IsNullOrEmpty(result.Data.jwtToken))
            {
                // Rotation: the server also returns a NEW refresh token.
                StorePair(result.Data, Remembered());
                PlayerPrefs.Save();
            }
            else
            {
                // Refresh token rejected/expired: session is over.
                ClearSession();
            }
        }

        private IEnumerator TrySilentLogin()
        {
            yield return StartCoroutine(EnsureFreshToken());
            if (string.IsNullOrEmpty(_jwt))
            {
                // No session (or the refresh token is dead): land on the auth form.
                if (_windowOpen)
                {
                    SetAuthMode(AuthMode.Login);
                    ShowAuth();
                    ShowMessage("Session expired. Please login again.");
                }
                yield break;
            }
            if (_windowOpen) yield return StartCoroutine(AfterLogin());
        }

        // ── Authed request helper: refreshes proactively, and once more on 401 ──
        private IEnumerator Authed<T>(Func<string, Action<DeRelayApiClient.ApiResult<T>>, IEnumerator> call,
            Action<DeRelayApiClient.ApiResult<T>> done)
        {
            yield return StartCoroutine(EnsureFreshToken());
            DeRelayApiClient.ApiResult<T> result = null;
            if (!string.IsNullOrEmpty(_jwt))
                yield return StartCoroutine(call(_jwt, r => result = r));
            if (result != null && !result.Ok && result.StatusCode == 401 && !string.IsNullOrEmpty(_refreshToken))
            {
                // Server says the JWT is dead (race with expiry): one forced
                // refresh, then retry the original call once.
                _jwt = ""; // force EnsureFreshToken to use the refresh token
                yield return StartCoroutine(EnsureFreshToken());
                if (!string.IsNullOrEmpty(_jwt))
                    yield return StartCoroutine(call(_jwt, r => result = r));
            }
            if ((result == null || (!result.Ok && result.StatusCode == 401)) && string.IsNullOrEmpty(_jwt))
            {
                ShowAuth();
                ShowMessage("Session expired. Please login again.");
            }
            done?.Invoke(result);
        }

        // ── Main view ──
        private void ShowMain()
        {
            _refs.AuthViewReference.style.display = DisplayStyle.None;
            _refs.MainViewReference.style.display = DisplayStyle.Flex;
            SetMainTab(MainTab.Friends);
        }

        private void SetMainTab(MainTab tab)
        {
            _mainTab = tab;
            SetTabActive(_refs.TabFriendsReference, tab == MainTab.Friends);
            SetTabActive(_refs.TabRequestsReference, tab == MainTab.Requests);
            SetTabActive(_refs.TabAccountReference, tab == MainTab.Account);
            _refs.RequestsSubReference.style.display = tab == MainTab.Requests ? DisplayStyle.Flex : DisplayStyle.None;
            _refs.SearchRowReference.style.display = tab == MainTab.Requests ? DisplayStyle.Flex : DisplayStyle.None;
            _refs.AccountRowReference.style.display = tab == MainTab.Account ? DisplayStyle.Flex : DisplayStyle.None;
            if (tab == MainTab.Account) _refs.SocialTitleReference.text = "Account";
            ClearMessage();
            if (tab == MainTab.Friends) StartCoroutine(LoadFriends());
            else if (tab == MainTab.Requests) SetRequestsSub(_requestsReceived);
            else
            {
                ClearList();
                _refs.SocialTitleReference.text = "Account";
            }
        }

        private void ClearList()
        {
            _refs.ContentListReference.Clear();
        }

        private void AddRow(string label, string buttonText, Action onButton)
        {
            var row = new VisualElement();
            row.AddToClassList("social-row");
            var text = new Label(label);
            text.AddToClassList("social-row-label");
            row.Add(text);
            if (!string.IsNullOrEmpty(buttonText))
            {
                var button = new Button(() => onButton?.Invoke()) { text = buttonText };
                button.AddToClassList("social-row-btn");
                row.Add(button);
            }
            _refs.ContentListReference.Add(row);
        }

        private IEnumerator LoadFriends()
        {
            ClearList();
            _refs.SocialTitleReference.text = "Friends";
            DeRelayApiClient.ApiResult<DeRelayApiClient.IdListDto> result = null;
            yield return StartCoroutine(Authed<DeRelayApiClient.IdListDto>(DeRelayApiClient.GetFriends, r => result = r));
            if (result == null || !result.Ok)
            {
                ShowMessage(result != null ? result.ErrorMessage : "Could not load friends.");
                yield break;
            }
            int[] ids = result.Data != null ? result.Data.Collect() : new int[0];
            if (ids.Length == 0) AddRow("No friends yet.", null, null);
            foreach (int id in ids)
            {
                int captured = id;
                AddRow("User #" + captured, "Remove", () => StartCoroutine(RemoveFriend(captured)));
            }
        }

        private IEnumerator RemoveFriend(int friendId)
        {
            ClearMessage();
            DeRelayApiClient.ApiResult<bool> result = null;
            yield return StartCoroutine(Authed<bool>((t, d) => DeRelayApiClient.RemoveFriend(t, friendId, d), r => result = r));
            if (result == null || !result.Ok)
            {
                ShowMessage(result != null ? result.ErrorMessage : "Could not remove friend.");
                yield break;
            }
            ShowMessage("Friend removed.");
            StartCoroutine(LoadFriends());
        }

        // ── Friend requests ──
        private void SetRequestsSub(bool received)
        {
            _requestsReceived = received;
            SetTabActive(_refs.ReqReceivedReference, received);
            SetTabActive(_refs.ReqSentReference, !received);
            RefreshSearchPlaceholder();
            StartCoroutine(LoadRequests());
        }

        private IEnumerator LoadRequests()
        {
            ClearList();
            _refs.SocialTitleReference.text = "Friend Requests";
            DeRelayApiClient.ApiResult<DeRelayApiClient.IdListDto> result = null;
            if (_requestsReceived)
                yield return StartCoroutine(Authed<DeRelayApiClient.IdListDto>(DeRelayApiClient.GetReceivedRequests, r => result = r));
            else
                yield return StartCoroutine(Authed<DeRelayApiClient.IdListDto>(DeRelayApiClient.GetSentRequests, r => result = r));
            if (result == null || !result.Ok)
            {
                ShowMessage(result != null ? result.ErrorMessage : "Could not load requests.");
                yield break;
            }
            int[] ids = result.Data != null ? result.Data.Collect() : new int[0];
            if (ids.Length == 0)
            {
                AddRow(_requestsReceived ? "No received requests." : "No sent requests.", null, null);
                yield break;
            }
            foreach (int id in ids)
            {
                int captured = id;
                if (_requestsReceived)
                {
                    var row = new VisualElement();
                    row.AddToClassList("social-row");
                    var text = new Label("User #" + captured);
                    text.AddToClassList("social-row-label");
                    row.Add(text);
                    var accept = new Button(() => StartCoroutine(AnswerRequest(true, captured))) { text = "Accept" };
                    accept.AddToClassList("social-row-btn");
                    var decline = new Button(() => StartCoroutine(AnswerRequest(false, captured))) { text = "Decline" };
                    decline.AddToClassList("social-row-btn");
                    row.Add(accept);
                    row.Add(decline);
                    _refs.ContentListReference.Add(row);
                }
                else
                {
                    AddRow("User #" + captured, "Cancel", () => StartCoroutine(AnswerRequest(false, captured)));
                }
            }
        }

        private IEnumerator AnswerRequest(bool accept, int userId)
        {
            ClearMessage();
            DeRelayApiClient.ApiResult<bool> result = null;
            if (accept)
                yield return StartCoroutine(Authed<bool>((t, d) => DeRelayApiClient.AcceptFriendRequest(t, userId, d), r => result = r));
            else
                yield return StartCoroutine(Authed<bool>((t, d) => DeRelayApiClient.DeclineFriendRequest(t, userId, d), r => result = r));
            if (result == null || !result.Ok)
            {
                ShowMessage(result != null ? result.ErrorMessage : "Request failed.");
                yield break;
            }
            StartCoroutine(LoadRequests());
        }

        // ── Search by ID ──
        private void SetupSearchPlaceholder()
        {
            RefreshSearchPlaceholder();
            _refs.SearchIdReference.RegisterValueChangedCallback(_ => RefreshSearchPlaceholder());
            _refs.SearchIdReference.RegisterCallback<FocusInEvent>(_ => RefreshSearchPlaceholder());
            _refs.SearchIdReference.RegisterCallback<FocusOutEvent>(_ => RefreshSearchPlaceholder());
        }

        private void RefreshSearchPlaceholder()
        {
            if (_refs.SearchPlaceholderReference == null) return;
            bool show = string.IsNullOrEmpty(_refs.SearchIdReference.value)
                && _refs.SearchIdReference.focusController != null
                && _refs.SearchIdReference.focusController.focusedElement != (VisualElement)_refs.SearchIdReference;
            _refs.SearchPlaceholderReference.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private IEnumerator OnSearchSend()
        {
            ClearMessage();
            if (!int.TryParse(_refs.SearchIdReference.value.Trim(), out int receiverId))
            {
                ShowMessage("Write a numeric ID to send friend request.");
                yield break;
            }
            DeRelayApiClient.ApiResult<bool> result = null;
            yield return StartCoroutine(Authed<bool>((t, d) => DeRelayApiClient.SendFriendRequest(t, receiverId, d), r => result = r));
            if (result == null || !result.Ok)
            {
                ShowMessage(result != null ? result.ErrorMessage : "Could not send request.");
                yield break;
            }
            _refs.SearchIdReference.value = "";
            RefreshSearchPlaceholder();
            ShowMessage("Friend request sent.");
            if (_mainTab == MainTab.Requests && !_requestsReceived) StartCoroutine(LoadRequests());
        }

        // ── Account ──
        private void OnLogout()
        {
            ClearSession();
            _refs.EmailReference.value = "";
            _refs.PasswordReference.value = "";
            SetAuthMode(AuthMode.Login);
            ShowAuth();
            ShowMessage("Logged out.");
        }

        private IEnumerator OnDeleteAccount()
        {
            // No confirmation by design: deletes immediately.
            ClearMessage();
            DeRelayApiClient.ApiResult<bool> result = null;
            yield return StartCoroutine(Authed<bool>(DeRelayApiClient.DeleteAccount, r => result = r));
            if (result == null || !result.Ok)
            {
                ShowMessage(result != null ? result.ErrorMessage : "Could not delete account.");
                yield break;
            }
            ClearSession();
            _refs.EmailReference.value = "";
            _refs.PasswordReference.value = "";
            SetAuthMode(AuthMode.Login);
            ShowAuth();
            ShowMessage("Account deleted.");
        }

        private void ShowMessage(string message)
        {
            if (_refs.MessageReference != null) _refs.MessageReference.text = message ?? "";
        }

        private void ClearMessage()
        {
            ShowMessage("");
        }
    }
}
