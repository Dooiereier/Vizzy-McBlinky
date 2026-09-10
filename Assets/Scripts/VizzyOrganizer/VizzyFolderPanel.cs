namespace Assets.Scripts.VizzyOrganizer
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Xml.Linq;
    using Assets.Scripts.Vizzy.UI;
    using Assets.Scripts.Vizzy.UI.Elements;
    using ModApi.Craft.Program;
    using ModApi.Craft.Program.Instructions;
    using UnityEngine;
    using UnityEngine.UI;

    /// <summary>
    /// A small "Jump to" button that opens a popup listing "All" / "Uncategorized" / the
    /// tree parsed out of "//Path/To" tag comments. Purely a navigation tool:
    /// selecting a row pans the canvas to that folder's chains without touching any block's
    /// visibility. Built entirely at runtime - no prefab/xml asset required.
    /// </summary>
    public sealed class VizzyFolderPanel : MonoBehaviour
    {
        private const float ButtonWidth = 135f;
        private const float ButtonHeight = 36f;
        private const float PopupWidth = 330f;
        private const float RowHeight = 36f;
        private const int FontSize = 20;
        private const float IndentPerDepth = 20f;

        private VizzyUIController _controller;
        private VizzyFolderIndex _index = new VizzyFolderIndex();
        private RectTransform _rowContainer;
        private GameObject _popup;

        // null = show everything. Otherwise the ids owned by the selected row (a folder's
        // full subtree, or the uncategorized set).
        private HashSet<int> _selectedIds;
        private readonly List<Toggle> _rowToggles = new List<Toggle>();

        public static VizzyFolderPanel AttachTo(VizzyUIController controller)
        {
            var existing = controller.GetComponentInChildren<VizzyFolderPanel>(true);
            if (existing != null)
                return existing;

            var buttonGo = new GameObject("VizzyFolderPanelButton", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(Image), typeof(Button));
            var buttonRect = buttonGo.GetComponent<RectTransform>();
            buttonRect.SetParent(controller.transform, false);
            buttonRect.anchorMin = new Vector2(1f, 1f);
            buttonRect.anchorMax = new Vector2(1f, 1f);
            buttonRect.pivot = new Vector2(1f, 1f);
            buttonRect.anchoredPosition = new Vector2(-8f, -8f);
            buttonRect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            buttonGo.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.15f, 0.9f);

            // Its own Canvas + GraphicRaycaster with a very high sort order guarantees this
            // button (and the popup nested under it) actually receives clicks, regardless of
            // whatever else is stacked in the existing Vizzy UI hierarchy at this point.
            var buttonCanvas = buttonGo.GetComponent<Canvas>();
            buttonCanvas.overrideSorting = true;
            buttonCanvas.sortingOrder = 30000;

            var buttonLabelGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
            var buttonLabelRect = buttonLabelGo.GetComponent<RectTransform>();
            buttonLabelRect.SetParent(buttonRect, false);
            buttonLabelRect.anchorMin = Vector2.zero;
            buttonLabelRect.anchorMax = Vector2.one;
            buttonLabelRect.offsetMin = Vector2.zero;
            buttonLabelRect.offsetMax = Vector2.zero;
            var buttonLabel = buttonLabelGo.GetComponent<Text>();
            buttonLabel.text = "Jump to";
            buttonLabel.alignment = TextAnchor.MiddleCenter;
            buttonLabel.color = Color.white;
            buttonLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            buttonLabel.fontSize = FontSize;
            buttonLabel.raycastTarget = false;

            var panel = buttonGo.AddComponent<VizzyFolderPanel>();
            panel._controller = controller;
            panel.BuildPopup(buttonRect);
            buttonGo.GetComponent<Button>().onClick.AddListener(panel.TogglePopup);
            return panel;
        }

        private void BuildPopup(RectTransform anchor)
        {
            _popup = new GameObject("VizzyFolderPanelPopup", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var popupRect = (RectTransform)_popup.transform;
            popupRect.SetParent(anchor, false);
            // Anchored to the button's bottom-right corner, growing left and down from
            // there, so it stays on screen with the button pinned to the top-right.
            popupRect.anchorMin = new Vector2(1f, 0f);
            popupRect.anchorMax = new Vector2(1f, 0f);
            popupRect.pivot = new Vector2(1f, 1f);
            popupRect.anchoredPosition = new Vector2(0f, -4f);
            popupRect.sizeDelta = new Vector2(PopupWidth, 0f);

            _popup.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.85f);

            var layout = _popup.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.spacing = 1f;

            var fitter = _popup.GetComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _rowContainer = popupRect;
            _popup.SetActive(false);
        }

        private void TogglePopup()
        {
            var opening = !_popup.activeSelf;
            _popup.SetActive(opening);

            // Layout groups skip inactive objects, so rows added while the popup was
            // hidden may not have sized correctly yet - force it now that it's visible.
            if (opening)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_rowContainer);
        }

        /// <summary>Rebuilds the folder tree from the currently loaded program and redraws the row list.</summary>
        public void Refresh()
        {
            var flightProgram = _controller.VizzyUI?.FlightProgram;

            // Comment text has no public accessor on the data model, and matching it up via
            // the rendered UI blocks turned out to be a dead end (separate object graph,
            // comments not even represented as BlockElementScript.Node at all in testing).
            // ProgramSerializer.SerializeFlightProgram gives an in-memory XML snapshot of the
            // *current* program (no disk I/O, no save required) with the same
            // <Comment id="..."><Constant text="..."/></Comment> shape as a saved craft file -
            // read the text straight off that, keyed by instruction id.
            var commentTextById = new Dictionary<int, string>();
            if (flightProgram != null)
            {
                var programXml = new ProgramSerializer().SerializeFlightProgram(flightProgram);
                foreach (var commentElement in programXml.Descendants("Comment"))
                {
                    if (!int.TryParse(commentElement.Attribute("id")?.Value, out var id))
                        continue;
                    commentTextById[id] = commentElement.Element("Constant")?.Attribute("text")?.Value;
                }
            }

            _index = VizzyFolderIndex.Build(flightProgram, comment =>
                commentTextById.TryGetValue(GetInstructionId(comment), out var text) ? text : null);

            Debug.Log($"[Vizzy McBlinky] Refresh: commentsInXml={commentTextById.Count}, uncategorized={_index.UncategorizedInstructionIds.Count}, folders={_index.Root.Children.Count}");

            foreach (Transform child in _rowContainer)
                Destroy(child.gameObject);
            _rowToggles.Clear();

            AddActionRow("Refresh", Refresh);
            AddRow("All", 0, null, null);
            foreach (var child in _index.Root.Children.Values.OrderBy(c => c.Name))
                AddFolderRows(child, 1, isTopLevelSegment: true, color: null);

            ApplyFilter();
        }

        private static int GetInstructionId(ProgramInstruction instruction) => ((IInstructionId)instruction).Id;

        // A plain action row (not a filter toggle) - e.g. "Refresh", so the tree can be
        // forced up to date without relying on catching every internal edit path.
        private void AddActionRow(string label, System.Action onClick)
        {
            var rowGo = new GameObject("Action_" + label, typeof(RectTransform), typeof(LayoutElement), typeof(Image), typeof(Button));
            var rowRect = rowGo.GetComponent<RectTransform>();
            rowRect.SetParent(_rowContainer, false);

            var layoutElement = rowGo.GetComponent<LayoutElement>();
            layoutElement.preferredHeight = RowHeight;
            layoutElement.flexibleWidth = 1f;

            // A Graphic on the row itself is what the raycaster actually hit-tests against -
            // the label's Text has raycastTarget=false, so without this the row has nothing
            // for clicks to land on and they fall through to whatever's behind it.
            var rowImage = rowGo.GetComponent<Image>();
            rowImage.color = new Color(1f, 1f, 1f, 0.02f);

            var textGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.SetParent(rowRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = Vector2.zero;

            var text = textGo.GetComponent<Text>();
            text.text = label;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(0.6f, 0.85f, 1f);
            text.fontStyle = FontStyle.Bold;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = FontSize;
            text.raycastTarget = false;

            var button = rowGo.GetComponent<Button>();
            button.targetGraphic = rowImage;
            button.onClick.AddListener(() => onClick());
        }

        // Every path segment gets its own row, however deep, with no skipping.
        //
        // color is assigned once, from the top-level segment's own name, and inherited
        // unchanged by every descendant - so everything under e.g. "Launch" shares one color
        // and reads as a group at a glance, distinct from "ECU"'s own color.
        private void AddFolderRows(VizzyFolderNode node, int depth, bool isTopLevelSegment, Color? color)
        {
            if (isTopLevelSegment)
                color = GetColorForName(node.Name);

            AddRow(node.Name, depth, new HashSet<int>(node.GetAllInstructionIds()), color);

            foreach (var child in node.Children.Values.OrderBy(c => c.Name))
                AddFolderRows(child, depth + 1, isTopLevelSegment: false, color);
        }

        // A stable, distinct color per name (same name always gets the same color, across
        // refreshes) - hashed into a hue rather than assigned by discovery order, so a
        // folder's color doesn't shift around as other folders are added/removed/renamed.
        private static Color GetColorForName(string name)
        {
            var hue = (name.GetHashCode() & 0x7fffffff) % 360 / 360f;
            return Color.HSVToRGB(hue, 0.55f, 1f);
        }

        private void AddRow(string label, int depth, HashSet<int> idsForThisRow, Color? labelColor)
        {
            var rowGo = new GameObject("Row_" + label, typeof(RectTransform), typeof(LayoutElement), typeof(Image), typeof(Toggle));
            var rowRect = rowGo.GetComponent<RectTransform>();
            rowRect.SetParent(_rowContainer, false);

            var layoutElement = rowGo.GetComponent<LayoutElement>();
            layoutElement.preferredHeight = RowHeight;
            layoutElement.flexibleWidth = 1f;

            // A Graphic on the row itself is what the raycaster actually hit-tests against -
            // the label's Text has raycastTarget=false, so without this the row has nothing
            // for clicks to land on and they fall through to whatever's behind it. Its color
            // also doubles as the "currently selected" highlight below.
            var rowImage = rowGo.GetComponent<Image>();
            var unselectedColor = new Color(1f, 1f, 1f, 0.02f);
            var selectedColor = new Color(1f, 1f, 1f, 0.18f);
            rowImage.color = idsForThisRow == null ? selectedColor : unselectedColor;

            var textGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.SetParent(rowRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f + depth * IndentPerDepth, 0f);
            textRect.offsetMax = Vector2.zero;

            var text = textGo.GetComponent<Text>();
            text.text = label;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = labelColor ?? Color.white;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = FontSize;
            // Bold at the top level, regular everywhere nested under it - makes root vs.
            // sub-entry obvious at a glance instead of relying on indentation alone.
            text.fontStyle = depth <= 1 ? FontStyle.Bold : FontStyle.Normal;
            text.raycastTarget = false;

            var toggle = rowGo.GetComponent<Toggle>();
            toggle.targetGraphic = rowImage;
            toggle.group = null;
            toggle.isOn = idsForThisRow == null;
            toggle.onValueChanged.AddListener(isOn =>
            {
                if (!isOn)
                    return;

                foreach (var other in _rowToggles)
                    if (other != toggle)
                    {
                        other.SetIsOnWithoutNotify(false);
                        other.GetComponent<Image>().color = unselectedColor;
                    }
                rowImage.color = selectedColor;

                _selectedIds = idsForThisRow;
                ApplyFilter();
            });
            _rowToggles.Add(toggle);
        }

        // The viewport's own pan position before we ever focused a folder - restored when
        // going back to "All", so the camera doesn't stay wherever it was left panned to.
        private Vector2? _originalViewportPosition;

        // 0 = bottom of the viewport, 1 = top. 0.9 puts the target near the very top, leaving
        // most of the screen below it for the chain content that extends downward from there.
        private const float TargetVerticalFraction = 0.9f;

        // 0 = left edge of the viewport, 1 = right. 0.4 puts the target left of center.
        private const float TargetHorizontalFraction = 0.4f;

        // A pure navigation tool now - selecting a folder never touches any block's
        // visibility, only pans the camera. Nothing is ever hidden or altered.
        private void ApplyFilter()
        {
            var programContainer = _controller.ProgramContainer;
            if (programContainer == null)
                return;

            if (_selectedIds == null)
            {
                PanToBoundsCenter(new List<Vector3>());
                return;
            }

            var chainIdGroups = VizzyFolderIndex.GetChainIdGroups(_controller.VizzyUI?.FlightProgram);
            var matchingChains = chainIdGroups.Where(chain => chain.Any(id => _selectedIds.Contains(id))).ToList();

            var idToTopLevelBlock = new Dictionary<int, BlockElementScript>();
            foreach (var block in programContainer.transform.GetComponentsInChildren<BlockElementScript>(true))
                if (block.Parent == null && block.Node is IInstructionId withId)
                    idToTopLevelBlock[withId.Id] = block;

            // One position per chain - its own topmost/head block - not one per instruction,
            // so a long chain doesn't outweigh a short one in the average.
            var chainHeadWorldPositions = new List<Vector3>();
            foreach (var chain in matchingChains)
            {
                var headId = chain.FirstOrDefault(idToTopLevelBlock.ContainsKey);
                if (idToTopLevelBlock.TryGetValue(headId, out var headBlock))
                    chainHeadWorldPositions.Add(headBlock.RectTransform.position);
            }

            PanToBoundsCenter(chainHeadWorldPositions);
        }

        // Pans the viewport to the mean position of each matching chain's own top block - each
        // chain counts once regardless of how many instructions it has, so this lands wherever
        // the relevant chains are clustered, rather than the bounding box's min/max midpoint
        // (which ignores clustering entirely and only depends on the two most extreme points).
        // Restores the original pan when back on "All".
        private void PanToBoundsCenter(List<Vector3> visiblePositions)
        {
            var viewportTransform = _controller.VizzyUI?.ProgramTransform;
            var programContainer = _controller.ProgramContainer;
            if (viewportTransform == null || programContainer == null)
                return;

            if (_selectedIds == null || visiblePositions.Count == 0)
            {
                if (_originalViewportPosition.HasValue)
                    viewportTransform.anchoredPosition = _originalViewportPosition.Value;
                _originalViewportPosition = null;
                return;
            }

            if (!_originalViewportPosition.HasValue)
                _originalViewportPosition = viewportTransform.anchoredPosition;

            var sum = Vector3.zero;
            foreach (var pos in visiblePositions)
                sum += pos;
            var meanWorld = sum / visiblePositions.Count;

            // The true, fixed screen reference is the outer viewport frame
            // (ProgramContainerScript's own rect - it receives pan/zoom input but doesn't
            // move itself), not viewportTransform's own current world position. The previous
            // version used viewportTransform.position as the target, but that point moves
            // every time the user scrolls (it IS the pan) - using a moving point as a fixed
            // target is exactly why the result depended on wherever the camera started.
            //
            // Biased toward the top (not dead center) - chain content extends downward from
            // its head, so putting the head near the top leaves more room below to actually
            // see it. Also biased left of center horizontally.
            var viewportRect = (RectTransform)programContainer.transform;
            var targetLocal = new Vector3(
                viewportRect.rect.xMin + viewportRect.rect.width * TargetHorizontalFraction,
                viewportRect.rect.yMin + viewportRect.rect.height * TargetVerticalFraction,
                0f);
            var fixedTargetWorld = viewportRect.TransformPoint(targetLocal);

            var deltaWorld = fixedTargetWorld - meanWorld;
            var parent = viewportTransform.parent;
            var deltaLocal = parent != null ? parent.InverseTransformVector(deltaWorld) : deltaWorld;
            viewportTransform.anchoredPosition += (Vector2)deltaLocal;
        }
    }
}
