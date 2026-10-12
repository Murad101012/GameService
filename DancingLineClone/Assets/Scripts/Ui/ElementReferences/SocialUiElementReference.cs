using UnityEngine;
using UnityEngine.UIElements;

namespace Ui.ElementReferences
{
    /// <summary>
    /// Get references from Menu UI Document for the social (DeRelay) overlay and cache them for others.
    /// </summary>
    /// <remarks>
    /// Follows the same convention as <see cref="MenuUiElementReference"/>:
    /// name of UI Element in UXML must match the variable name below.
    /// </remarks>
    public class SocialUiElementReference : MonoBehaviour
    {
        public VisualElement Root;

        private readonly string _profileButtonName = "Btn_Profile";
        public Button ProfileButtonReference;

        private readonly string _socialLayerName = "Social_Layer";
        public VisualElement SocialLayerReference;

        private readonly string _socialWindowName = "Social_Window";
        public VisualElement SocialWindowReference;

        private readonly string _socialTitleName = "Lbl_SocialTitle";
        public Label SocialTitleReference;

        private readonly string _authViewName = "Social_AuthView";
        public VisualElement AuthViewReference;

        private readonly string _authTabsName = "Social_AuthTabs";
        public VisualElement AuthTabsReference;

        private readonly string _showLoginName = "Btn_ShowLogin";
        public Button ShowLoginReference;

        private readonly string _showRegisterName = "Btn_ShowRegister";
        public Button ShowRegisterReference;

        private readonly string _authRow1Name = "Social_AuthRow1";
        public VisualElement AuthRow1Reference;

        private readonly string _authRow2Name = "Social_AuthRow2";
        public VisualElement AuthRow2Reference;

        private readonly string _authRow3Name = "Social_AuthRow3";
        public VisualElement AuthRow3Reference;

        private readonly string _emailName = "Fld_Email";
        public TextField EmailReference;

        private readonly string _passwordName = "Fld_Password";
        public TextField PasswordReference;

        private readonly string _firstNameName = "Fld_FirstName";
        public TextField FirstNameReference;

        private readonly string _lastNameName = "Fld_LastName";
        public TextField LastNameReference;

        private readonly string _nickNameName = "Fld_NickName";
        public TextField NickNameReference;

        private readonly string _birthRowName = "Social_BirthRow";
        public VisualElement BirthRowReference;

        private readonly string _birthCaptionName = "Lbl_BirthCaption";
        public Label BirthCaptionReference;

        private readonly string _birthYearName = "Fld_BirthYear";
        public TextField BirthYearReference;

        private readonly string _birthMonthName = "Fld_BirthMonth";
        public TextField BirthMonthReference;

        private readonly string _birthDayName = "Fld_BirthDay";
        public TextField BirthDayReference;

        private readonly string _gender0Name = "Btn_Gender0";
        public Button Gender0Reference;

        private readonly string _gender1Name = "Btn_Gender1";
        public Button Gender1Reference;

        private readonly string _gender2Name = "Btn_Gender2";
        public Button Gender2Reference;

        private readonly string _processName = "Btn_Process";
        public Button ProcessReference;

        private readonly string _rememberRowName = "Social_RememberRow";
        public VisualElement RememberRowReference;

        private readonly string _rememberMeName = "Tgl_RememberMe";
        public Toggle RememberMeReference;

        private readonly string _mainViewName = "Social_MainView";
        public VisualElement MainViewReference;

        private readonly string _tabFriendsName = "Btn_TabFriends";
        public Button TabFriendsReference;

        private readonly string _tabRequestsName = "Btn_TabRequests";
        public Button TabRequestsReference;

        private readonly string _tabAccountName = "Btn_TabAccount";
        public Button TabAccountReference;

        private readonly string _requestsSubName = "Social_RequestsSub";
        public VisualElement RequestsSubReference;

        private readonly string _reqReceivedName = "Btn_ReqReceived";
        public Button ReqReceivedReference;

        private readonly string _reqSentName = "Btn_ReqSent";
        public Button ReqSentReference;

        private readonly string _searchRowName = "Social_SearchRow";
        public VisualElement SearchRowReference;

        private readonly string _searchIdName = "Fld_SearchId";
        public TextField SearchIdReference;

        private readonly string _searchPlaceholderName = "Lbl_SearchPlaceholder";
        public Label SearchPlaceholderReference;

        private readonly string _searchSendName = "Btn_SearchSend";
        public Button SearchSendReference;

        private readonly string _accountRowName = "Social_AccountRow";
        public VisualElement AccountRowReference;

        private readonly string _logoutName = "Btn_Logout";
        public Button LogoutReference;

        private readonly string _deleteAccountName = "Btn_DeleteAccount";
        public Button DeleteAccountReference;

        private readonly string _contentListName = "Social_ContentList";
        public ScrollView ContentListReference;

        private readonly string _messageName = "Lbl_SocialMessage";
        public Label MessageReference;

        private readonly string _backToMenuName = "Btn_BackToMenu";
        public Button BackToMenuReference;

        public bool CheckResult { get; private set; }
        public bool CheckFinished { get; private set; }

        private void Awake()
        {
            // Prefer the UIDocument on this object, but fall back to the scene's
            // Menu document: if these scripts were dropped on a separate object,
            // RequireComponent would otherwise attach an EMPTY UIDocument and every
            // lookup below would fail while looking perfectly configured.
            var uiDocument = GetComponent<UIDocument>();
            if (uiDocument == null || uiDocument.rootVisualElement == null
                || uiDocument.rootVisualElement.childCount == 0)
            {
                uiDocument = FindFirstObjectByType<UIDocument>();
            }
            if (uiDocument == null)
            {
                Debug.LogError($"{name}: No UIDocument found in scene for SocialUiElementReference.");
                CheckFinished = true;
                return;
            }
            Root = uiDocument.rootVisualElement;
            Initialization();
        }

        private void Initialization()
        {
            ProfileButtonReference = Root.Q<Button>(_profileButtonName);
            SocialLayerReference = Root.Q<VisualElement>(_socialLayerName);
            SocialWindowReference = Root.Q<VisualElement>(_socialWindowName);
            SocialTitleReference = Root.Q<Label>(_socialTitleName);
            AuthViewReference = Root.Q<VisualElement>(_authViewName);
            AuthTabsReference = Root.Q<VisualElement>(_authTabsName);
            ShowLoginReference = Root.Q<Button>(_showLoginName);
            ShowRegisterReference = Root.Q<Button>(_showRegisterName);
            AuthRow1Reference = Root.Q<VisualElement>(_authRow1Name);
            AuthRow2Reference = Root.Q<VisualElement>(_authRow2Name);
            AuthRow3Reference = Root.Q<VisualElement>(_authRow3Name);
            EmailReference = Root.Q<TextField>(_emailName);
            PasswordReference = Root.Q<TextField>(_passwordName);
            FirstNameReference = Root.Q<TextField>(_firstNameName);
            LastNameReference = Root.Q<TextField>(_lastNameName);
            NickNameReference = Root.Q<TextField>(_nickNameName);
            BirthRowReference = Root.Q<VisualElement>(_birthRowName);
            BirthCaptionReference = Root.Q<Label>(_birthCaptionName);
            BirthYearReference = Root.Q<TextField>(_birthYearName);
            BirthMonthReference = Root.Q<TextField>(_birthMonthName);
            BirthDayReference = Root.Q<TextField>(_birthDayName);
            Gender0Reference = Root.Q<Button>(_gender0Name);
            Gender1Reference = Root.Q<Button>(_gender1Name);
            Gender2Reference = Root.Q<Button>(_gender2Name);
            ProcessReference = Root.Q<Button>(_processName);
            RememberRowReference = Root.Q<VisualElement>(_rememberRowName);
            RememberMeReference = Root.Q<Toggle>(_rememberMeName);
            MainViewReference = Root.Q<VisualElement>(_mainViewName);
            TabFriendsReference = Root.Q<Button>(_tabFriendsName);
            TabRequestsReference = Root.Q<Button>(_tabRequestsName);
            TabAccountReference = Root.Q<Button>(_tabAccountName);
            RequestsSubReference = Root.Q<VisualElement>(_requestsSubName);
            ReqReceivedReference = Root.Q<Button>(_reqReceivedName);
            ReqSentReference = Root.Q<Button>(_reqSentName);
            SearchRowReference = Root.Q<VisualElement>(_searchRowName);
            SearchIdReference = Root.Q<TextField>(_searchIdName);
            SearchPlaceholderReference = Root.Q<Label>(_searchPlaceholderName);
            SearchSendReference = Root.Q<Button>(_searchSendName);
            AccountRowReference = Root.Q<VisualElement>(_accountRowName);
            LogoutReference = Root.Q<Button>(_logoutName);
            DeleteAccountReference = Root.Q<Button>(_deleteAccountName);
            ContentListReference = Root.Q<ScrollView>(_contentListName);
            MessageReference = Root.Q<Label>(_messageName);
            BackToMenuReference = Root.Q<Button>(_backToMenuName);

            CheckResult = true;
            Validate(ProfileButtonReference);
            Validate(SocialLayerReference);
            Validate(SocialWindowReference);
            Validate(SocialTitleReference);
            Validate(AuthViewReference);
            Validate(AuthTabsReference);
            Validate(ShowLoginReference);
            Validate(ShowRegisterReference);
            Validate(AuthRow1Reference);
            Validate(AuthRow2Reference);
            Validate(AuthRow3Reference);
            Validate(EmailReference);
            Validate(PasswordReference);
            Validate(FirstNameReference);
            Validate(LastNameReference);
            Validate(NickNameReference);
            Validate(BirthRowReference);
            Validate(BirthCaptionReference);
            Validate(BirthYearReference);
            Validate(BirthMonthReference);
            Validate(BirthDayReference);
            Validate(Gender0Reference);
            Validate(Gender1Reference);
            Validate(Gender2Reference);
            Validate(ProcessReference);
            Validate(RememberRowReference);
            Validate(RememberMeReference);
            Validate(MainViewReference);
            Validate(TabFriendsReference);
            Validate(TabRequestsReference);
            Validate(TabAccountReference);
            Validate(RequestsSubReference);
            Validate(ReqReceivedReference);
            Validate(ReqSentReference);
            Validate(SearchRowReference);
            Validate(SearchIdReference);
            Validate(SearchPlaceholderReference);
            Validate(SearchSendReference);
            Validate(AccountRowReference);
            Validate(LogoutReference);
            Validate(DeleteAccountReference);
            Validate(ContentListReference);
            Validate(MessageReference);
            Validate(BackToMenuReference);

            if (!CheckResult)
            {
                Debug.LogError($"{name}: One or more social visual Elements couldn't find" +
                               " (Please check if corresponding element have string typo)");
            }

            CheckFinished = true;
        }

        private void Validate(VisualElement visualElement)
        {
            if (visualElement != null) return;
            Debug.LogWarning($"{name}: {nameof(visualElement)} is null");
            CheckResult = false;
        }
    }
}
