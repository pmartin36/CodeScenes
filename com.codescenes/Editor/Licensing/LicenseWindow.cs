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

        [MenuItem("CodeScenes/License", false, 20)]
        public static void Open()
        {
            GetWindow<LicenseWindow>("CodeScenes License");
        }

        private LicenseWindowModel _model;

        private VisualElement _entryGroup;
        private VisualElement _licensedGroup;
        private VisualElement _seatsGroup;
        private VisualElement _seatList;
        private TextField _keyField;
        private Button _activateButton;
        private Button _buyButton;
        private Label _trialLabel;
        private Label _messageLabel;

        private IVisualElementScheduledItem _activatingAnim;
        private int _activatingDots;

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
            _seatsGroup = rootVisualElement.Q<VisualElement>("seatsGroup");
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
                bool activating = _model.View == LicenseWindowView.Activating;
                _activateButton.SetEnabled(!activating);
                if (activating)
                {
                    StartActivatingAnimation();
                }
                else
                {
                    StopActivatingAnimation();
                    _activateButton.text = "Activate";
                }
            }

            if (_messageLabel != null)
            {
                string message = _model.Message ?? string.Empty;
                _messageLabel.text = message;
                SetVisible(_messageLabel, message.Length > 0);
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

            // Hide the whole Seats section when there is nothing to manage, so the no-license
            // view is not padded out by an empty inset box.
            SetVisible(_seatsGroup, _model.Seats.Length > 0);
        }

        private VisualElement BuildSeatRow(Seat seat)
        {
            var row = new VisualElement { name = "seatRow" };
            row.AddToClassList("seat-row");

            bool isCurrent = _model.IsCurrentMachine(seat);
            if (isCurrent)
            {
                row.AddToClassList("seat-row--current");
            }

            string suffix = isCurrent ? " (this machine)" : string.Empty;
            var label = new Label($"{seat.label} ({seat.os}){suffix}");
            label.AddToClassList("seat-row__label");
            row.Add(label);

            var remove = new Button(() => _ = _model.RemoveSeatAsync(seat.hash)) { text = "Remove" };
            remove.AddToClassList("seat-remove");
            row.Add(remove);

            return row;
        }

        // Animate the disabled Activate button as the in-progress indicator: "Activating" with a
        // cycling 1..3 dot tail, so the click has visible feedback while the request is in flight.
        private void StartActivatingAnimation()
        {
            if (_activatingAnim != null)
            {
                return;
            }

            _activatingDots = 0;
            _activateButton.text = "Activating";
            _activatingAnim = _activateButton.schedule.Execute(() =>
            {
                _activatingDots = (_activatingDots % 3) + 1;
                _activateButton.text = "Activating" + new string('.', _activatingDots);
            }).Every(300);
        }

        private void StopActivatingAnimation()
        {
            _activatingAnim?.Pause();
            _activatingAnim = null;
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
