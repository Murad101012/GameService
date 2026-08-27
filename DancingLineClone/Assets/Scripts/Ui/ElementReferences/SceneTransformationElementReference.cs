using UnityEngine;
using UnityEngine.UIElements;

namespace Ui.ElementReferences
{
    /// <summary>
    /// Keeping the reference to the elements in loading screen
    /// </summary>
    /// <remarks>Logic works exactly same as <see cref="MenuUiElementReference"/></remarks>
    public class SceneTransformationElementReference : MonoBehaviour
    {
        public VisualElement Root;

        private readonly string _blackLayerReferenceName = "Black_Layer";
        public VisualElement BlackLayerReference;
        
        /// <summary>
        /// Scripts those are using <see cref="SceneTransformationElementReference"/> must implement this event, to prevent null UI Element problems.
        /// True: All Visual Elements found in UIDocument/rootVisualElement
        /// False: One or more visual Elements couldn't find (Please check if corresponding element have string typo)
        /// </summary>
        public bool CheckResult { get; private set; }

        public bool CheckFinished { get; private set; }

        private void Awake()
        {
            Root = GetComponent<UIDocument>().rootVisualElement;
            Initialization();
        }
        
        private void Initialization()
        {
            //List all elements to get reference
            //LevelLoadButtonReference = _root.Q<Button>(_levelLoadButtonName);
            BlackLayerReference = Root.Q<VisualElement>(_blackLayerReferenceName);
            
            //We're beginning with true, otherwise each time null check make checkResult to true even one of the Null check find problem,
            //it might be overridden  it 
            CheckResult = true;
            
            //Validate(LevelLoadButtonReference);
            Validate(BlackLayerReference);
            
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
        /// Validate(myElementReference);
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