using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SceneBuilder.Editor.Licensing
{
    // The one screen a paying customer sees: key entry / activate / seat management. All
    // behavior lives in LicenseWindowModel; this view only builds the UXML tree and binds it.
    public sealed class LicenseWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.codescenes/Editor/Licensing/LicenseWindow.uxml";
        private const string UssPath = "Packages/com.codescenes/Editor/Licensing/LicenseWindow.uss";

        [MenuItem("CodeScenes/License")]
        public static void Open()
        {
            GetWindow<LicenseWindow>("CodeScenes License");
        }

        private LicenseWindowModel _model;

        private VisualElement _entryGroup;
        private VisualElement _licensedGroup;
        private VisualElement _seatList;
        private TextField _keyField;
        private Button _activateButton;
        private Button _buyButton;
        private Label _trialLabel;
        private Label _messageLabel;

        private void CreateGUI()
        {
            _model = new LicenseWindowModel();

            VisualTreeAsset uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                Debug.LogWarning($"CodeScenes: could not load {UxmlPath}");
                return;
            }

            uxml.CloneTree(rootVisualElement);

            StyleSheet uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null)
            {
                rootVisualElement.styleSheets.Add(uss);
            }

            _entryGroup = rootVisualElement.Q<VisualElement>("entryGroup");
            _licensedGroup = rootVisualElement.Q<VisualElement>("licensedGroup");
            _seatList = rootVisualElement.Q<VisualElement>("seatList");
            _keyField = rootVisualElement.Q<TextField>("keyField");
            _activateButton = rootVisualElement.Q<Button>("activateButton");
            _buyButton = rootVisualElement.Q<Button>("buyButton");
            _trialLabel = rootVisualElement.Q<Label>("trialLabel");
            _messageLabel = rootVisualElement.Q<Label>("messageLabel");

            if (_activateButton != null)
            {
                _activateButton.clicked += () => _ = _model.ActivateAsync(_keyField != null ? _keyField.value : string.Empty);
            }

            if (_buyButton != null)
            {
                _buyButton.clicked += _model.Buy;
            }

            _model.ModelChanged += Refresh;
            _model.Initialize();
            Refresh();
        }

        private void Refresh()
        {
            SetVisible(_entryGroup, _model.View != LicenseWindowView.Licensed);
            SetVisible(_licensedGroup, _model.View == LicenseWindowView.Licensed);

            if (_activateButton != null)
            {
                _activateButton.SetEnabled(_model.View != LicenseWindowView.Activating);
            }

            if (_messageLabel != null)
            {
                _messageLabel.text = _model.Message ?? string.Empty;
            }

            if (_trialLabel != null)
            {
                _trialLabel.text = _model.TrialDaysRemaining > 0
                    ? $"{_model.TrialDaysRemaining} day(s) left in your trial."
                    : string.Empty;
            }

            if (_seatList != null)
            {
                _seatList.Clear();
                foreach (Seat seat in _model.Seats)
                {
                    _seatList.Add(BuildSeatRow(seat));
                }
            }
        }

        private VisualElement BuildSeatRow(Seat seat)
        {
            var row = new VisualElement { name = "seatRow" };
            row.style.flexDirection = FlexDirection.Row;

            string suffix = _model.IsCurrentMachine(seat) ? " (this machine)" : string.Empty;
            row.Add(new Label($"{seat.label} ({seat.os}){suffix}"));

            var remove = new Button(() => _ = _model.RemoveSeatAsync(seat.hash)) { text = "Remove" };
            row.Add(remove);

            return row;
        }

        private static void SetVisible(VisualElement element, bool visible)
        {
            if (element == null)
            {
                return;
            }

            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
