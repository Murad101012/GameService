using UnityEngine;
using UnityEngine.UIElements;

namespace Ui.ElementReferences
{
    /// <summary>
    /// Get references from Menu UI Document and cache them for provide others
    /// </summary>
    /// <remarks>
    /// Centralizes string-based UI queries to prevent typos. 
    /// Use <see cref="CheckResult"/> to verify that all queried elements were successfully found in the UIDocument.
    /// To successfully find Visual Element from UXML, the name of UI Element must match variable name
    /// at <see cref="Initialization"/>. For each referencing it <c>MUST BE</c> adding <see cref="Validate"/>,
    /// otherwise type-checking will be skips for that element.
    /// </remarks>
    /// <example>If in UXML file it's written as <c><![CDATA[<ui:Button name="Cont_DragZone"/>]]></c> 
    /// then <see cref="_dragZoneName"/> = "Cont_DragZone";</example>
    [RequireComponent(typeof(UIDocument))]
    public class MenuUiElementReference: MonoBehaviour
    {
        public VisualElement Root;
        
        private readonly string _dragZoneName = "Cont_DragZone";
        public VisualElement DragZoneReference;

        private readonly string _carouselName = "Cont_Carousel";
        public VisualElement CarouselReference;
        
        private readonly string _levelLabelName = "Lbl_LevelTitle";
        public Label LevelLabelReference;

        private readonly string _debugLabelName = "Lbl_DebugText";
        public Label DebugLabelReference;
        
        private readonly string _blackScreenName = "Black_Screen";
        public VisualElement BlackScreenReference;
        
        private readonly string _sliderCarouselName = "Slider_Carousel";
        public Slider SliderCarouselReference;
        
        private readonly string _unityDraggerName = "unity-dragger";
        public VisualElement UnityDraggerReference;
        
        private readonly string _unityDragContainerName = "unity-drag-container";
        public VisualElement UnityDragContainerReference;
        
        private readonly string _unityTrackerName = "unity-tracker";
        public VisualElement UnityTrackerReference;
        
        public VisualElement[] LevelButtonsReferences;
        
        /// <summary>
        /// Scripts those are using <see cref="MenuUiElementReference"/> must implement this event, to prevent null UI Element problems.
        /// True: All Visual Elements found in UIDocument/rootVisualElement
        /// False: One or more visual Elements couldn't find (Please check if corresponding element have string typo)
        /// </summary>
        public bool CheckResult { get; private set; }

        public bool CheckFinished { get; private set; }

        private void Awake()
        {
            //After we get rootVisualElement, we begin to processing if all elements mentioned in here are available (not null)
            Root = GetComponent<UIDocument>().rootVisualElement;
            
            Initialization();
        }
        
        private void Initialization()
        {
            //List all elements to get reference
            //LevelLoadButtonReference = _root.Q<Button>(_levelLoadButtonName);
            LevelLabelReference = Root.Q<Label>(_levelLabelName);
            CarouselReference = Root.Q<VisualElement>(_carouselName);
            DebugLabelReference = Root.Q<Label>(_debugLabelName);
            DragZoneReference = Root.Q<VisualElement>(_dragZoneName);
            BlackScreenReference = Root.Q<VisualElement>(_blackScreenName);
            SliderCarouselReference = Root.Q<Slider>(_sliderCarouselName);
            UnityDraggerReference = Root.Q<VisualElement>(_unityDraggerName);
            UnityDragContainerReference = Root.Q<VisualElement>(_unityDragContainerName);
            UnityTrackerReference = Root.Q<VisualElement>(_unityTrackerName);
            LevelButtonsReferences = new VisualElement[CarouselReference.childCount];
            for (int i = 0; i < CarouselReference.childCount; i++)
            {
                LevelButtonsReferences[i] = CarouselReference[i]; 
            }
            
            
            //We're beginning with true, otherwise each time null check make checkResult to true even one of the Null check find problem,
            //it might be overridden  it 
            CheckResult = true;
            
            //Validate(LevelLoadButtonReference, nameof(LevelLoadButtonReference));
            Validate(LevelLabelReference);
            Validate(CarouselReference);
            Validate(DebugLabelReference);
            Validate(DragZoneReference);
            Validate(BlackScreenReference);
            Validate(SliderCarouselReference);
            Validate(UnityDraggerReference);
            Validate(UnityTrackerReference);
            Validate(UnityDragContainerReference);
            for (int i = 0; i < CarouselReference.childCount; i++)
            {
                Validate(LevelButtonsReferences[i]); 
            }
            
            
            if (!CheckResult)
            {
                Debug.LogError($"{name}: One or more visual Elements couldn't find" +
                               " (Please check if corresponding element have string typo)");
            }

            CheckFinished = true;
        }

        /// <summary>
        /// Checks if a VisualElement is null and updates the global <see cref="CheckResult"/>.
        /// </summary>
        /// <param name="visualElement">The element to check.</param>
        /// <remarks>
        /// <para>If the element is null, a <c>Debug.LogWarning</c> is issued and <see cref="CheckResult"/> is set to false.</para>
        /// <example>
        /// <code>
        /// Validate(myButton, nameof(myButton));
        /// </code>
        /// </example>
        /// </remarks>

        private void Validate(VisualElement visualElement)
        {
            if (visualElement != null) return;
            Debug.LogWarning($"{name}: {nameof(visualElement)} is null");
            CheckResult = false;
        }
    }
}