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
        private const string PrefsToken = "DeRelay.Token";
        private const string PrefsUsername = "DeRelay.Username";
        private const string PrefsPassword = "DeRelay.Password";

        private SocialUiElementReference _refs;
        private GlobalAudioPlayer _audio;
        private UIDocument _uiDocument;

        private enum AuthMode { Login, Register }
        private enum MainTab { Friends, Requests, Account }

        private AuthMode _authMode = AuthMode.Register; // default: Register
        private MainTab _mainTab = MainTab.Friends;
        private bool _requestsReceived = true; // default: You Received
        private int _gender;
        private bool _windowOpen;

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
            if (HasToken()) ShowMain();
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
            SetTabActive(_refs.ShowLoginReference, isLogin);
            SetTabActive(_refs.ShowRegisterReference, !isLogin);
            // Row 1 (username + password) always visible. Rows 2-3 + birth row are register-only.
            _refs.AuthRow2Reference.style.display = isLogin ? DisplayStyle.None : DisplayStyle.Flex;
            _refs.AuthRow3Reference.style.display = isLogin ? DisplayStyle.None : DisplayStyle.Flex;
            _refs.BirthRowReference.style.display = isLogin ? DisplayStyle.None : DisplayStyle.Flex;
            _refs.BirthCaptionReference.style.display = isLogin ? DisplayStyle.None : DisplayStyle.Flex;
            _refs.Gender0Reference.style.display = isLogin ? DisplayStyle.None : DisplayStyle.Flex;
            _refs.Gender1Reference.style.display = isLogin ? DisplayStyle.None : DisplayStyle.Flex;
            _refs.Gender2Reference.style.display = isLogin ? DisplayStyle.None : DisplayStyle.Flex;
            var genderRow = _refs.Gender0Reference.parent;
            if (genderRow != null) genderRow.style.display = isLogin ? DisplayStyle.None : DisplayStyle.Flex;
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
            if (_authMode == AuthMode.Login) yield return StartCoroutine(DoLogin(null, null));
            else yield return StartCoroutine(DoRegister());
        }

        private IEnumerator DoRegister()
        {
            string username = _refs.UserNameReference.value.Trim();
            string password = _refs.PasswordReference.value;
            string first = _refs.FirstNameReference.value.Trim();
            string last = _refs.LastNameReference.value.Trim();
            string nick = _refs.NickNameReference.value.Trim();
            if (!TryReadBirthDate(out DateTime dob)) yield break;
            var body = new DeRelayApiClient.RegisterRequest
            {
                UserName = username,
                Password = password,
                FirstName = first,
                LastName = last,
                NickName = nick,
                Gender = _gender,
                // Trailing Z: marks the value UTC so the server timestamptz accepts it.
                DateOfBirth = dob.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
            DeRelayApiClient.ApiResult<int> result = null;
            yield return StartCoroutine(DeRelayApiClient.Register(body, r => result = r));
            if (result == null || !result.Ok)
            {
                ShowMessage(result != null ? result.ErrorMessage : "Register failed.");
                yield break;
            }
            // Success: switch to login and invite the user to log in.
            SetAuthMode(AuthMode.Login);
            _refs.UserNameReference.value = username;
            _refs.PasswordReference.value = "";
            ShowMessage("Register successful, please login.");
        }

        private IEnumerator DoLogin(string usernameOverride, string passwordOverride)
        {
            string username = usernameOverride ?? _refs.UserNameReference.value.Trim();
            string password = passwordOverride ?? _refs.PasswordReference.value;
            var body = new DeRelayApiClient.LoginRequest { UserName = username, Password = password };
            DeRelayApiClient.ApiResult<string> result = null;
            yield return StartCoroutine(DeRelayApiClient.Login(body, r => result = r));
            if (result == null || !result.Ok)
            {
                ShowMessage(result != null ? result.ErrorMessage : "Login failed.");
                yield break;
            }
            // Save token + credentials only after a successful login.
            PlayerPrefs.SetString(PrefsToken, result.Data);
            PlayerPrefs.SetString(PrefsUsername, username);
            PlayerPrefs.SetString(PrefsPassword, password);
            PlayerPrefs.Save();
            ShowMain();
        }

        // ── Authed request helper: on 401 retry once via saved credentials ──
        private bool HasToken()
        {
            return !string.IsNullOrEmpty(PlayerPrefs.GetString(PrefsToken, ""));
        }

        private string Token()
        {
            return PlayerPrefs.GetString(PrefsToken, "");
        }

        private IEnumerator Authed<T>(Func<string, Action<DeRelayApiClient.ApiResult<T>>, IEnumerator> call,
            Action<DeRelayApiClient.ApiResult<T>> done)
        {
            DeRelayApiClient.ApiResult<T> result = null;
            yield return StartCoroutine(call(Token(), r => result = r));
            if (result != null && !result.Ok && result.StatusCode == 401)
            {
                // Token expired (or any auth error): re-login with saved
                // credentials, then retry the original call once.
                string username = PlayerPrefs.GetString(PrefsUsername, "");
                string password = PlayerPrefs.GetString(PrefsPassword, "");
                if (!string.IsNullOrEmpty(username))
                {
                    DeRelayApiClient.ApiResult<string> login = null;
                    yield return StartCoroutine(DeRelayApiClient.Login(
                        new DeRelayApiClient.LoginRequest { UserName = username, Password = password },
                        r => login = r));
                    if (login != null && login.Ok)
                    {
                        PlayerPrefs.SetString(PrefsToken, login.Data);
                        PlayerPrefs.Save();
                        yield return StartCoroutine(call(Token(), r => result = r));
                    }
                    else
                    {
                        ShowAuth();
                        ShowMessage(login != null ? login.ErrorMessage : "Session expired. Please login again.");
                        done?.Invoke(result);
                        yield break;
                    }
                }
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
            PlayerPrefs.DeleteKey(PrefsToken);
            PlayerPrefs.DeleteKey(PrefsUsername);
            PlayerPrefs.DeleteKey(PrefsPassword);
            PlayerPrefs.Save();
            _refs.UserNameReference.value = "";
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
            PlayerPrefs.DeleteKey(PrefsToken);
            PlayerPrefs.DeleteKey(PrefsUsername);
            PlayerPrefs.DeleteKey(PrefsPassword);
            PlayerPrefs.Save();
            _refs.UserNameReference.value = "";
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
