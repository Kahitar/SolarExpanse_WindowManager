#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Manager;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SolarExpanse.WindowManager
{
    public static class SolarExpanseWindowManager
    {
        internal const float ButtonSize = 48f;
        internal const float WindowDropOffset = 4f;
        private const float ButtonSpacing = 2f;
        private const float ButtonGroupPadding = 4f;
        private const float NotificationButtonGap = 10f;
        private const float ButtonTopVisualOffset = 5f;

        private static readonly FieldInfo FieldShowNotificationHistory =
            typeof(NotificationManager).GetField("showNotificationHistory",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FieldNotificationHistory =
            typeof(NotificationManager).GetField("notificationHistory",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FieldNotificationPrefab =
            typeof(NotificationManager).GetField("notificationUIPrefab",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type TypeShowToolTip =
            typeof(NotificationManager).Assembly.GetType("ShowToolTip");
        private static readonly Type TypeToolTipWindow =
            typeof(NotificationManager).Assembly.GetType("ToolTipWindow");
        private static readonly PropertyInfo PropertyTooltipCustomText =
            TypeShowToolTip?.GetProperty("CustomTextFromCode",
                BindingFlags.Instance | BindingFlags.Public);
        private static readonly FieldInfo FieldTooltipShowCustomFromCode =
            TypeShowToolTip?.GetField("showCustomFromCode",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly string[] TooltipTemplateFieldNames =
        {
            "afterTime",
            "toUpper",
            "advance",
            "lineSpacing",
            "alignment",
            "maxTextWidth",
            "tooltipAnchor",
        };

        private static readonly Dictionary<string, UiWindowHandleImpl> Handles =
            new Dictionary<string, UiWindowHandleImpl>();
        private static readonly List<UiWindowHandleImpl> SortedHandles =
            new List<UiWindowHandleImpl>();

        private static ManualLogSource _log =
            BepInEx.Logging.Logger.CreateLogSource("SolarExpanse.WindowManager");
        private static NotificationManager _notificationManager;
        private static Button _showNotificationButton;
        private static RectTransform _showNotificationButtonRect;
        private static GameObject _notificationHistoryTemplate;
        private static Component _nativeTooltipWindow;
        private static Canvas _canvas;
        private static RectTransform _canvasRect;
        private static TMP_FontAsset _font;
        private static GameObject _buttonGroupObject;
        private static RectTransform _buttonGroupRect;
        private static HorizontalLayoutGroup _buttonGroupLayout;
        private static UiButtonGroupMover _buttonGroupMover;
        private static ButtonVisualStyle _buttonVisualStyle;
        private static Vector2 _lastCanvasSize;
        private static Rect _lastVisibleCanvasRect;
        private static bool _buttonGroupUserPositioned;
        private static bool _buttonGroupAttachedRight;
        private static bool _buttonGroupAttachedTop;
        private static Vector2 _buttonGroupEdgeOffsets;
        private static bool _buttonGroupPlacementNeedsRestore;
        private static bool _recoveringButtonGroupPosition;
        private static Sprite _generatedButtonSprite;
        private static Sprite _generatedActiveButtonSprite;
        private static Sprite _generatedGroupFrameSprite;
        private static int _focusCounter;

        internal static Color MutedStatusTextColor { get; private set; } =
            new Color(0.53f, 0.53f, 0.53f, 1f);

        public static IUiWindowHandle RegisterWindow(UiWindowRegistration registration)
        {
            if (!ValidateRegistration(registration))
                return null;

            UiWindowRegistration normalized = NormalizeRegistration(registration);
            var handle = new UiWindowHandleImpl(normalized);
            Handles.Add(normalized.Id, handle);
            RebuildSortedHandles();

            if (CanRealize)
            {
                EnsureButtonGroup();
                RealizeHandle(handle);
                RefreshButtonGroup();
            }

            return handle;
        }

        public static bool UnregisterWindow(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;

            UiWindowHandleImpl handle;
            if (!Handles.TryGetValue(id, out handle))
                return false;

            handle.DestroyRealization();
            Handles.Remove(id);
            RebuildSortedHandles();
            RefreshButtonGroup();
            return true;
        }

        public static bool TryGetWindow(string id, out IUiWindowHandle handle)
        {
            UiWindowHandleImpl impl = null;
            bool found = !string.IsNullOrEmpty(id) && Handles.TryGetValue(id, out impl);
            handle = impl;
            return found;
        }

        internal static void SetLog(ManualLogSource log)
        {
            if (log != null)
                _log = log;
        }

        internal static void Realize(NotificationManager notificationManager)
        {
            try
            {
                Button showButton = FieldShowNotificationHistory?.GetValue(notificationManager) as Button;
                if (showButton == null)
                {
                    _log.LogError("[SEWM] showNotificationHistory not found; UI registrations remain unrealized");
                    return;
                }

                GameObject historyTemplate =
                    FieldNotificationHistory?.GetValue(notificationManager) as GameObject;
                if (historyTemplate == null)
                {
                    _log.LogError("[SEWM] notificationHistory not found; UI registrations remain unrealized");
                    return;
                }

                Canvas canvas = showButton.GetComponentInParent<Canvas>();
                RectTransform canvasRect = canvas != null ? canvas.GetComponent<RectTransform>() : null;
                if (canvas == null || canvasRect == null)
                {
                    _log.LogError("[SEWM] notification button canvas not found; UI registrations remain unrealized");
                    return;
                }

                bool canvasChanged = _canvas != null && _canvas != canvas;
                if (canvasChanged)
                    DestroyAllRealizations();

                _notificationManager = notificationManager;
                _showNotificationButton = showButton;
                _showNotificationButtonRect = showButton.GetComponent<RectTransform>();
                _notificationHistoryTemplate = historyTemplate;
                _canvas = canvas;
                _canvasRect = canvasRect;
                _font = FindFontAsset(notificationManager, historyTemplate);
                _buttonVisualStyle = DiscoverButtonStyle(canvas, showButton);
                _lastCanvasSize = _canvasRect.rect.size;
                _lastVisibleCanvasRect = GetVisibleCanvasRect();

                if (SortedHandles.Count == 0)
                {
                    RefreshButtonGroup();
                    return;
                }

                EnsureButtonGroup();
                foreach (UiWindowHandleImpl handle in SortedHandles)
                    RealizeHandle(handle);
                RefreshButtonGroup();

                _log.LogInfo($"[SEWM] Realized {SortedHandles.Count} registered UI window(s)");
            }
            catch (Exception e)
            {
                _log.LogError($"[SEWM] realization exception: {e}");
            }
        }

        internal static void InternalUpdate()
        {
            if (_canvasRect == null)
                return;

            Vector2 size = _canvasRect.rect.size;
            Rect visibleRect = GetVisibleCanvasRect();
            if (size != _lastCanvasSize || !Approximately(_lastVisibleCanvasRect, visibleRect))
            {
                _lastCanvasSize = size;
                _lastVisibleCanvasRect = visibleRect;
                _buttonGroupPlacementNeedsRestore = true;
                foreach (UiWindowHandleImpl handle in SortedHandles)
                    handle.ClampWindow();
            }

            EnsureButtonGroupVisible();
        }

        internal static bool CloseTopmostWindow()
        {
            UiWindowHandleImpl topmost = null;
            foreach (UiWindowHandleImpl handle in SortedHandles)
            {
                if (!handle.IsOpen)
                    continue;
                if (topmost == null || handle.FocusOrder > topmost.FocusOrder)
                    topmost = handle;
            }

            if (topmost == null)
                return false;

            topmost.Close();
            return true;
        }

        internal static int NextFocusOrder() => ++_focusCounter;

        internal static void KeepButtonGroupOnTop()
        {
            if (_buttonGroupObject != null)
                _buttonGroupObject.transform.SetAsLastSibling();
        }

        internal static void MoveButtonGroup(Vector2 anchoredPosition, bool storeUserPosition)
        {
            if (_buttonGroupRect == null || _canvasRect == null)
                return;
            if (!IsFinite(anchoredPosition))
            {
                RecoverButtonGroupPosition();
                return;
            }

            Vector2 previousPosition = _buttonGroupRect.anchoredPosition;
            _buttonGroupRect.anchoredPosition = anchoredPosition;
            ClampButtonGroupToVisibleCanvas();
            if (!IsFinite(_buttonGroupRect.anchoredPosition))
                return;

            if (storeUserPosition)
            {
                Vector2 movement = _buttonGroupRect.anchoredPosition - previousPosition;
                if (movement.sqrMagnitude > 0f)
                {
                    foreach (UiWindowHandleImpl handle in SortedHandles)
                        handle.MoveOpenWindowBy(movement);
                }

                _buttonGroupUserPositioned = true;
                _buttonGroupPlacementNeedsRestore = false;
                StoreButtonGroupEdgePlacement();
            }
        }

        internal static void EnsureButtonGroupVisible()
        {
            Rect visibleRect = GetVisibleCanvasRect();
            if (IsFinite(visibleRect) && !Approximately(_lastVisibleCanvasRect, visibleRect))
            {
                _lastVisibleCanvasRect = visibleRect;
                _buttonGroupPlacementNeedsRestore = true;
            }

            if (_buttonGroupPlacementNeedsRestore)
            {
                _buttonGroupPlacementNeedsRestore = false;
                PositionButtonGroup();
            }

            ClampButtonGroupToVisibleCanvas();
            KeepNativeTooltipOnTop();
        }

        private static void KeepNativeTooltipOnTop()
        {
            if (TypeToolTipWindow == null)
                return;

            if (_nativeTooltipWindow == null)
            {
                foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(TypeToolTipWindow))
                {
                    Component candidate = obj as Component;
                    if (candidate == null)
                        continue;

                    if (_nativeTooltipWindow == null)
                        _nativeTooltipWindow = candidate;
                    if (candidate.GetComponentInParent<Canvas>() == _canvas)
                    {
                        _nativeTooltipWindow = candidate;
                        break;
                    }
                }
            }

            if (_nativeTooltipWindow == null || !_nativeTooltipWindow.gameObject.activeInHierarchy)
                return;

            Canvas tooltipCanvas = _nativeTooltipWindow.GetComponentInParent<Canvas>();
            Transform renderRoot = _nativeTooltipWindow.transform;
            if (tooltipCanvas != null)
            {
                while (renderRoot.parent != null && renderRoot.parent != tooltipCanvas.transform)
                    renderRoot = renderRoot.parent;
            }

            renderRoot.SetAsLastSibling();
        }

        internal static Vector2 CanvasLocalPointFromWorld(Vector3 worldPoint)
        {
            if (_canvasRect == null)
                return Vector2.zero;

            Camera cam = _canvas != null && _canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : _canvas?.worldCamera;
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(cam, worldPoint);
            Vector2 localPoint;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRect, screenPoint, cam, out localPoint);
            return localPoint;
        }

        private static bool CanRealize =>
            _canvas != null &&
            _canvasRect != null &&
            _showNotificationButtonRect != null &&
            _notificationHistoryTemplate != null;

        internal static Component FindNativeTooltipTemplate()
        {
            if (TypeShowToolTip == null)
                return null;

            Component tooltip = _showNotificationButton?.GetComponent(TypeShowToolTip);
            if (tooltip != null)
                return tooltip;

            tooltip = _showNotificationButton?.GetComponentInChildren(TypeShowToolTip, includeInactive: true);
            if (tooltip != null)
                return tooltip;

            if (_canvas == null)
                return null;

            Component fallback = null;
            foreach (Component candidate in _canvas.GetComponentsInChildren(TypeShowToolTip, includeInactive: true))
            {
                if (candidate == null ||
                    candidate.gameObject.name.StartsWith("SEWM_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (fallback == null)
                    fallback = candidate;
                if (candidate.GetComponentInParent<Button>() != null)
                    return candidate;
            }

            return fallback;
        }

        internal static Component AddNativeTooltip(GameObject target, string text)
        {
            if (target == null || TypeShowToolTip == null ||
                PropertyTooltipCustomText == null || FieldTooltipShowCustomFromCode == null)
            {
                return null;
            }

            Component tooltip = target.AddComponent(TypeShowToolTip);
            Component template = FindNativeTooltipTemplate();
            if (template != null)
            {
                foreach (string fieldName in TooltipTemplateFieldNames)
                {
                    FieldInfo field = TypeShowToolTip.GetField(fieldName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null)
                        field.SetValue(tooltip, field.GetValue(template));
                }
            }

            PropertyTooltipCustomText.SetValue(tooltip, text, null);
            FieldTooltipShowCustomFromCode.SetValue(tooltip, true);
            return tooltip;
        }

        private static bool ValidateRegistration(UiWindowRegistration registration)
        {
            bool valid = true;
            if (registration == null)
            {
                _log.LogError("[SEWM] Rejecting null UI window registration");
                return false;
            }

            if (string.IsNullOrEmpty(registration.Id))
            {
                _log.LogError("[SEWM] Rejecting UI window registration with missing Id");
                valid = false;
            }
            else if (Handles.ContainsKey(registration.Id))
            {
                _log.LogError($"[SEWM] Rejecting duplicate UI window registration Id '{registration.Id}'");
                valid = false;
            }

            if (string.IsNullOrEmpty(registration.DisplayName))
            {
                _log.LogError($"[SEWM] Rejecting UI window registration '{registration.Id}' with missing DisplayName");
                valid = false;
            }

            if (registration.Icon == null)
            {
                _log.LogError($"[SEWM] Rejecting UI window registration '{registration.Id}' with missing Icon fallback");
                valid = false;
            }

            if (registration.BuildContent == null)
            {
                _log.LogError($"[SEWM] Rejecting UI window registration '{registration.Id}' with missing BuildContent");
                valid = false;
            }

            return valid;
        }

        private static UiWindowRegistration NormalizeRegistration(UiWindowRegistration registration)
        {
            Vector2 defaultSize = registration.DefaultWindowSize;
            if (defaultSize.x <= 0f || defaultSize.y <= 0f)
                defaultSize = new Vector2(720f, 300f);

            Vector2 minimumSize = registration.MinimumWindowSize;
            if (minimumSize.x <= 0f || minimumSize.y <= 0f)
                minimumSize = new Vector2(500f, 180f);

            defaultSize = new Vector2(
                Mathf.Max(defaultSize.x, minimumSize.x),
                Mathf.Max(defaultSize.y, minimumSize.y));

            return new UiWindowRegistration
            {
                Id = registration.Id,
                DisplayName = registration.DisplayName,
                Order = registration.Order,
                Icon = registration.Icon,
                GameIconNames = NormalizeGameIconNames(registration.GameIconNames),
                IconTint = registration.IconTint,
                DefaultWindowSize = defaultSize,
                MinimumWindowSize = minimumSize,
                BuildContent = registration.BuildContent,
                OnOpen = registration.OnOpen,
                OnClose = registration.OnClose,
            };
        }

        private static string[] NormalizeGameIconNames(IEnumerable<string> names)
            => names == null
                ? Array.Empty<string>()
                : names.Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        private static void RebuildSortedHandles()
        {
            SortedHandles.Clear();
            SortedHandles.AddRange(Handles.Values.OrderBy(h => h.Registration.Order)
                .ThenBy(h => h.Registration.Id, StringComparer.Ordinal));
        }

        private static void RealizeHandle(UiWindowHandleImpl handle)
        {
            try
            {
                if (!handle.IsRealized)
                    handle.Realize(_canvas, _canvasRect, _buttonGroupRect.transform,
                        _notificationHistoryTemplate, _font, _buttonVisualStyle, _log);
            }
            catch (Exception e)
            {
                _log.LogError($"[SEWM] Failed to realize '{handle.Id}': {e}");
            }
        }

        private static void EnsureButtonGroup()
        {
            if (!CanRealize || SortedHandles.Count == 0)
                return;

            if (_buttonGroupObject != null && _buttonGroupRect != null)
                return;

            _buttonGroupObject = new GameObject("SEWM_ButtonGroup", typeof(RectTransform));
            _buttonGroupObject.transform.SetParent(_canvas.transform, false);
            _buttonGroupObject.transform.SetAsLastSibling();

            var layoutElement = _buttonGroupObject.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;

            _buttonGroupRect = _buttonGroupObject.GetComponent<RectTransform>();
            _buttonGroupRect.anchorMin = new Vector2(0.5f, 0.5f);
            _buttonGroupRect.anchorMax = new Vector2(0.5f, 0.5f);
            _buttonGroupRect.pivot = new Vector2(1f, 1f);
            _buttonGroupRect.anchoredPosition = new Vector2(-9999f, -9999f);

            Image groupImage = _buttonGroupObject.AddComponent<Image>();
            groupImage.sprite = _buttonVisualStyle.GroupSprite;
            groupImage.type = Image.Type.Sliced;
            groupImage.color = Color.white;
            groupImage.raycastTarget = true;

            _buttonGroupMover = _buttonGroupObject.AddComponent<UiButtonGroupMover>();
            _buttonGroupObject.AddComponent<UiButtonGroupViewportGuard>();

            _buttonGroupLayout = _buttonGroupObject.AddComponent<HorizontalLayoutGroup>();
            _buttonGroupLayout.childControlWidth = false;
            _buttonGroupLayout.childControlHeight = false;
            _buttonGroupLayout.childForceExpandWidth = false;
            _buttonGroupLayout.childForceExpandHeight = false;
            _buttonGroupLayout.spacing = ButtonSpacing;
            _buttonGroupLayout.padding = new RectOffset(
                (int)ButtonGroupPadding,
                (int)ButtonGroupPadding,
                (int)ButtonGroupPadding,
                (int)ButtonGroupPadding);
        }

        private static void RefreshButtonGroup()
        {
            if (SortedHandles.Count == 0)
            {
                if (_buttonGroupObject != null)
                    UnityEngine.Object.Destroy(_buttonGroupObject);
                _buttonGroupObject = null;
                _buttonGroupRect = null;
                _buttonGroupLayout = null;
                _buttonGroupMover = null;
                return;
            }

            if (!CanRealize)
                return;

            EnsureButtonGroup();
            if (_buttonGroupRect == null)
                return;

            int count = 0;
            foreach (UiWindowHandleImpl handle in SortedHandles)
            {
                if (!handle.IsRealized || handle.ButtonRect == null)
                    continue;
                handle.ButtonRect.SetParent(_buttonGroupRect, false);
                handle.ButtonRect.SetSiblingIndex(count);
                count++;
            }

            float width = ButtonGroupPadding * 2f + count * ButtonSize +
                Mathf.Max(0, count - 1) * ButtonSpacing;
            float height = ButtonGroupPadding * 2f + ButtonSize;
            _buttonGroupRect.sizeDelta = new Vector2(width, height);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_buttonGroupRect);
            _buttonGroupPlacementNeedsRestore = true;
            PositionButtonGroup();
        }

        private static void PositionButtonGroup()
        {
            if (_buttonGroupRect == null || _showNotificationButtonRect == null || _canvasRect == null)
                return;

            if (_buttonGroupUserPositioned)
            {
                RestoreButtonGroupFromEdgePlacement();
                _buttonGroupObject.transform.SetAsLastSibling();
                return;
            }

            Vector3[] corners = new Vector3[4];
            _showNotificationButtonRect.GetWorldCorners(corners);
            Vector2 notificationTopLeft = CanvasLocalPointFromWorld(corners[1]);
            MoveButtonGroup(new Vector2(
                notificationTopLeft.x - NotificationButtonGap,
                notificationTopLeft.y - ButtonTopVisualOffset), storeUserPosition: false);
            _buttonGroupObject.transform.SetAsLastSibling();
        }

        private static ButtonVisualStyle DiscoverButtonStyle(Canvas canvas, Button notificationButton)
        {
            Button sourceButton = FindDarkButtonStyleSource(canvas, notificationButton);
            Image image = sourceButton != null ? sourceButton.GetComponent<Image>() : null;
            bool copyGameStyle = image != null && IsDarkButtonImage(image);

            if (!copyGameStyle)
            {
                return new ButtonVisualStyle
                {
                    Sprite = GeneratedButtonSprite,
                    ActiveSprite = GeneratedActiveButtonSprite,
                    Type = Image.Type.Sliced,
                    Material = null,
                    NormalColor = Color.white,
                    HoverColor = new Color(1.18f, 1.18f, 1.18f, 1f),
                    PressedColor = new Color(0.72f, 0.78f, 0.86f, 1f),
                    ActiveColor = Color.white,
                    GroupSprite = GeneratedGroupFrameSprite,
                };
            }

            Color normal = EnsureUsableDarkColor(image.color);
            Color hover = new Color(
                Mathf.Min(1f, normal.r * 1.2f + 0.03f),
                Mathf.Min(1f, normal.g * 1.2f + 0.03f),
                Mathf.Min(1f, normal.b * 1.2f + 0.03f),
                normal.a);
            Color pressed = new Color(normal.r * 0.72f, normal.g * 0.72f, normal.b * 0.72f, normal.a);

            return new ButtonVisualStyle
            {
                Sprite = image.sprite,
                ActiveSprite = image.sprite,
                Type = image.sprite != null ? image.type : Image.Type.Sliced,
                Material = image.material,
                NormalColor = normal,
                HoverColor = hover,
                PressedColor = pressed,
                ActiveColor = new Color(0.10f, 0.30f, 0.50f, 1f),
                GroupSprite = GeneratedGroupFrameSprite,
            };
        }

        private static Button FindDarkButtonStyleSource(Canvas canvas, Button notificationButton)
        {
            Button best = null;
            float bestScore = float.NegativeInfinity;
            foreach (Button button in canvas.GetComponentsInChildren<Button>(includeInactive: true))
            {
                Image img = button != null ? button.GetComponent<Image>() : null;
                if (img == null || !IsDarkButtonImage(img))
                    continue;

                RectTransform rt = button.GetComponent<RectTransform>();
                float sizeScore = 0f;
                if (rt != null)
                {
                    Vector2 size = rt.rect.size;
                    if (size.x >= 24f && size.x <= 96f && size.y >= 24f && size.y <= 96f)
                        sizeScore = 1f;
                }

                float brightness = Brightness(img.color);
                float score = (1f - brightness) * 3f + sizeScore + (img.sprite != null ? 1f : 0f);
                if (button == notificationButton || button.name.IndexOf("notification", StringComparison.OrdinalIgnoreCase) >= 0)
                    score -= 2f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = button;
                }
            }

            return best;
        }

        private static TMP_FontAsset FindFontAsset(NotificationManager nm, GameObject historyGO)
        {
            TextMeshProUGUI src = historyGO.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
            if (src?.font != null)
                return src.font;

            try
            {
                object prefab = FieldNotificationPrefab?.GetValue(nm);
                if (prefab != null)
                {
                    FieldInfo textField = prefab.GetType().GetField("text",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    src = textField?.GetValue(prefab) as TextMeshProUGUI;
                    if (src?.font != null)
                        return src.font;
                }
            }
            catch (Exception e)
            {
                _log.LogWarning($"[SEWM] font discovery fallback: {e.Message}");
            }

            _log.LogWarning("[SEWM] No TextMeshPro font found from notification UI");
            return null;
        }

        private static void DestroyAllRealizations()
        {
            foreach (UiWindowHandleImpl handle in SortedHandles)
                handle.DestroyRealization();

            if (_buttonGroupObject != null)
                UnityEngine.Object.Destroy(_buttonGroupObject);

            _buttonGroupObject = null;
            _buttonGroupRect = null;
            _buttonGroupLayout = null;
            _buttonGroupMover = null;
            _canvas = null;
            _canvasRect = null;
            _showNotificationButton = null;
            _showNotificationButtonRect = null;
            _notificationHistoryTemplate = null;
            _nativeTooltipWindow = null;
            _font = null;
            _buttonGroupPlacementNeedsRestore = false;
        }

        internal static string SanitizeId(string id)
        {
            char[] chars = id.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                    chars[i] = '_';
            }
            return new string(chars);
        }

        internal static float ClampEvenIfTooSmall(float value, float min, float max)
        {
            if (min > max)
                return (min + max) * 0.5f;
            return Mathf.Clamp(value, min, max);
        }

        private static void StoreButtonGroupEdgePlacement()
        {
            if (_buttonGroupRect == null || _canvasRect == null)
                return;

            Rect visibleRect = GetVisibleCanvasRect();
            Bounds bounds = GetButtonGroupBounds();
            if (!IsFinite(visibleRect) || !IsFinite(bounds))
                return;

            float leftOffset = Mathf.Max(0f, bounds.min.x - visibleRect.xMin);
            float rightOffset = Mathf.Max(0f, visibleRect.xMax - bounds.max.x);
            float bottomOffset = Mathf.Max(0f, bounds.min.y - visibleRect.yMin);
            float topOffset = Mathf.Max(0f, visibleRect.yMax - bounds.max.y);
            _buttonGroupAttachedRight = rightOffset < leftOffset;
            _buttonGroupAttachedTop = topOffset < bottomOffset;
            _buttonGroupEdgeOffsets = new Vector2(
                _buttonGroupAttachedRight ? rightOffset : leftOffset,
                _buttonGroupAttachedTop ? topOffset : bottomOffset);
        }

        private static void RestoreButtonGroupFromEdgePlacement()
        {
            if (_buttonGroupRect == null || _canvasRect == null)
                return;

            Rect visibleRect = GetVisibleCanvasRect();
            Bounds bounds = GetButtonGroupBounds();
            if (!IsFinite(visibleRect) || !IsFinite(bounds))
            {
                RecoverButtonGroupPosition();
                return;
            }

            Vector2 offsets = new Vector2(
                IsFinite(_buttonGroupEdgeOffsets.x) ? Mathf.Max(0f, _buttonGroupEdgeOffsets.x) : 0f,
                IsFinite(_buttonGroupEdgeOffsets.y) ? Mathf.Max(0f, _buttonGroupEdgeOffsets.y) : 0f);
            Vector2 targetCenter = new Vector2(
                _buttonGroupAttachedRight
                    ? visibleRect.xMax - offsets.x - bounds.extents.x
                    : visibleRect.xMin + offsets.x + bounds.extents.x,
                _buttonGroupAttachedTop
                    ? visibleRect.yMax - offsets.y - bounds.extents.y
                    : visibleRect.yMin + offsets.y + bounds.extents.y);
            MoveButtonGroup(_buttonGroupRect.anchoredPosition +
                targetCenter - (Vector2)bounds.center, storeUserPosition: false);
        }

        private static void ClampButtonGroupToVisibleCanvas()
        {
            if (_buttonGroupRect == null || _canvasRect == null)
                return;

            if (!IsFinite(_buttonGroupRect.anchoredPosition))
            {
                RecoverButtonGroupPosition();
                return;
            }

            Rect visibleRect = GetVisibleCanvasRect();
            Bounds bounds = GetButtonGroupBounds();
            if (!IsFinite(visibleRect) || !IsFinite(bounds))
            {
                RecoverButtonGroupPosition();
                return;
            }

            Vector2 correction = new Vector2(
                GetBoundsCorrection(bounds.min.x, bounds.max.x, visibleRect.xMin, visibleRect.xMax),
                GetBoundsCorrection(bounds.min.y, bounds.max.y, visibleRect.yMin, visibleRect.yMax));
            if (correction.sqrMagnitude > 0.0001f)
                _buttonGroupRect.anchoredPosition += correction;
        }

        private static Rect GetVisibleCanvasRect()
        {
            Rect fallback = _canvasRect != null ? _canvasRect.rect : default(Rect);
            if (_canvas == null || _canvasRect == null)
                return fallback;

            Rect pixelRect = _canvas.pixelRect;
            if (pixelRect.width <= 0f || pixelRect.height <= 0f)
                return fallback;

            Camera cam = _canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : _canvas.worldCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, pixelRect.min, cam, out Vector2 min) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, pixelRect.max, cam, out Vector2 max))
                return fallback;

            return Rect.MinMaxRect(
                Mathf.Min(min.x, max.x),
                Mathf.Min(min.y, max.y),
                Mathf.Max(min.x, max.x),
                Mathf.Max(min.y, max.y));
        }

        private static Bounds GetButtonGroupBounds() =>
            RectTransformUtility.CalculateRelativeRectTransformBounds(_canvasRect, _buttonGroupRect);

        private static float GetBoundsCorrection(float boundsMin, float boundsMax,
            float visibleMin, float visibleMax)
        {
            float boundsSize = boundsMax - boundsMin;
            float visibleSize = visibleMax - visibleMin;
            if (boundsSize > visibleSize)
                return (visibleMin + visibleMax - boundsMin - boundsMax) * 0.5f;
            if (boundsMin < visibleMin)
                return visibleMin - boundsMin;
            if (boundsMax > visibleMax)
                return visibleMax - boundsMax;
            return 0f;
        }

        private static void RecoverButtonGroupPosition()
        {
            if (_buttonGroupRect == null || _recoveringButtonGroupPosition)
                return;

            _recoveringButtonGroupPosition = true;
            try
            {
                _buttonGroupPlacementNeedsRestore = true;
                _buttonGroupRect.anchoredPosition = Vector2.zero;
                ClampButtonGroupToVisibleCanvas();
            }
            finally
            {
                _recoveringButtonGroupPosition = false;
            }
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector2 value) =>
            IsFinite(value.x) && IsFinite(value.y);

        private static bool IsFinite(Rect value) =>
            IsFinite(value.xMin) && IsFinite(value.xMax) &&
            IsFinite(value.yMin) && IsFinite(value.yMax);

        private static bool IsFinite(Bounds value) =>
            IsFinite(value.center.x) && IsFinite(value.center.y) && IsFinite(value.center.z) &&
            IsFinite(value.extents.x) && IsFinite(value.extents.y) && IsFinite(value.extents.z);

        private static bool Approximately(Rect a, Rect b) =>
            Mathf.Approximately(a.xMin, b.xMin) &&
            Mathf.Approximately(a.xMax, b.xMax) &&
            Mathf.Approximately(a.yMin, b.yMin) &&
            Mathf.Approximately(a.yMax, b.yMax);

        private static bool IsDarkButtonImage(Image image)
        {
            if (image == null || image.color.a < 0.25f)
                return false;
            return Brightness(image.color) < 0.55f;
        }

        private static Color EnsureUsableDarkColor(Color color)
        {
            if (color.a < 0.25f || Brightness(color) > 0.55f)
                return new Color(0.08f, 0.10f, 0.12f, 0.96f);
            return color;
        }

        private static float Brightness(Color color) =>
            color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;

        private static Sprite GeneratedButtonSprite
        {
            get
            {
                if (_generatedButtonSprite == null)
                    _generatedButtonSprite = BuildBeveledSprite("SEWM_Button_Normal",
                        new Color(0.055f, 0.065f, 0.075f, 0.98f),
                        new Color(0.42f, 0.47f, 0.50f, 1f),
                        new Color(0.010f, 0.014f, 0.018f, 1f),
                        new Color(0.14f, 0.16f, 0.18f, 1f));
                return _generatedButtonSprite;
            }
        }

        private static Sprite GeneratedActiveButtonSprite
        {
            get
            {
                if (_generatedActiveButtonSprite == null)
                    _generatedActiveButtonSprite = BuildBeveledSprite("SEWM_Button_Active",
                        new Color(0.045f, 0.075f, 0.11f, 0.98f),
                        new Color(0.44f, 0.61f, 0.72f, 1f),
                        new Color(0.01f, 0.03f, 0.05f, 1f),
                        new Color(0.05f, 0.30f, 0.58f, 1f));
                return _generatedActiveButtonSprite;
            }
        }

        private static Sprite GeneratedGroupFrameSprite
        {
            get
            {
                if (_generatedGroupFrameSprite == null)
                    _generatedGroupFrameSprite = BuildBeveledSprite("SEWM_Group_Frame",
                        new Color(0.015f, 0.022f, 0.028f, 0.90f),
                        new Color(0.36f, 0.43f, 0.46f, 1f),
                        new Color(0.005f, 0.007f, 0.010f, 1f),
                        new Color(0.09f, 0.12f, 0.14f, 1f));
                return _generatedGroupFrameSprite;
            }
        }

        private static Sprite BuildBeveledSprite(string name, Color fill, Color topLeft,
            Color bottomRight, Color inner)
        {
            const int size = 32;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Color c = fill;
                    bool edge = x == 0 || y == 0 || x == size - 1 || y == size - 1;
                    bool innerEdge = x == 1 || y == 1 || x == size - 2 || y == size - 2;
                    if (edge)
                        c = x == size - 1 || y == 0 ? bottomRight : topLeft;
                    else if (innerEdge)
                        c = inner;
                    pixels[y * size + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            tex.name = name;
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(3f, 3f, 3f, 3f));
            sprite.name = name;
            return sprite;
        }

        internal static Sprite ResolveRegistrationIcon(UiWindowRegistration registration)
        {
            foreach (string spriteName in registration.GameIconNames ?? Array.Empty<string>())
            {
                Sprite sprite = FindGameSprite(spriteName);
                if (sprite != null)
                    return sprite;

                sprite = BuildSpriteFromTmpSpriteAsset(spriteName);
                if (sprite != null)
                    return sprite;
            }

            return registration.Icon;
        }

        private static Sprite FindGameSprite(string spriteName)
            => Resources.FindObjectsOfTypeAll<Sprite>()
                .FirstOrDefault(s => s != null && string.Equals(s.name, spriteName, StringComparison.OrdinalIgnoreCase));

        private static Sprite BuildSpriteFromTmpSpriteAsset(string spriteName)
        {
            foreach (TMP_SpriteAsset asset in Resources.FindObjectsOfTypeAll<TMP_SpriteAsset>())
            {
                object character = FindTmpSpriteCharacter(asset, spriteName);
                object glyph = ReadMemberValue(character, "glyph", "Glyph");
                Texture2D texture = ReadMemberValue(asset, "spriteSheet", "SpriteSheet", "atlasTexture", "AtlasTexture") as Texture2D;
                if (glyph == null || texture == null || !TryReadGlyphRect(glyph, out Rect rect))
                    continue;

                Sprite sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), Mathf.Max(rect.width, rect.height));
                sprite.name = spriteName;
                return sprite;
            }

            return null;
        }

        private static object FindTmpSpriteCharacter(TMP_SpriteAsset asset, string spriteName)
        {
            IEnumerable<TMP_SpriteCharacter> characters = asset?.spriteCharacterTable;
            if (characters == null)
                return null;

            foreach (TMP_SpriteCharacter character in characters)
            {
                if (string.Equals(character?.name, spriteName, StringComparison.OrdinalIgnoreCase))
                    return character;
            }

            return null;
        }

        private static bool TryReadGlyphRect(object glyph, out Rect rect)
        {
            rect = default;
            object glyphRect = ReadMemberValue(glyph, "glyphRect", "GlyphRect");
            if (glyphRect == null)
                return false;

            float x = ReadFloatMember(glyphRect, "x", "X");
            float y = ReadFloatMember(glyphRect, "y", "Y");
            float width = ReadFloatMember(glyphRect, "width", "Width");
            float height = ReadFloatMember(glyphRect, "height", "Height");
            if (width <= 0f || height <= 0f)
                return false;

            rect = new Rect(x, y, width, height);
            return true;
        }

        private static object ReadMemberValue(object target, params string[] names)
        {
            if (target == null)
                return null;

            Type type = target.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (string name in names)
            {
                PropertyInfo property = type.GetProperty(name, flags);
                if (property != null)
                    return property.GetValue(target, null);

                FieldInfo field = type.GetField(name, flags);
                if (field != null)
                    return field.GetValue(target);
            }

            return null;
        }

        private static float ReadFloatMember(object target, params string[] names)
            => Convert.ToSingle(ReadMemberValue(target, names), System.Globalization.CultureInfo.InvariantCulture);

        [HarmonyPatch(typeof(NotificationManager), "Awake")]
        private static class NotificationManagerAwakePatch
        {
            [HarmonyPostfix]
            private static void Postfix(NotificationManager __instance) => Realize(__instance);
        }
    }

    public sealed class UiWindowRegistration
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public int Order { get; set; }
        public Sprite Icon { get; set; }
        public string[] GameIconNames { get; set; }
        public Color? IconTint { get; set; }
        public Vector2 DefaultWindowSize { get; set; }
        public Vector2 MinimumWindowSize { get; set; }
        public Action<UiWindowContext> BuildContent { get; set; }
        public Action<UiWindowContext> OnOpen { get; set; }
        public Action<UiWindowContext> OnClose { get; set; }
    }

    public sealed class UiWindowContext
    {
        internal UiWindowContext(string id, string displayName, Canvas canvas, TMP_FontAsset font,
            GameObject windowObject, RectTransform windowRect, RectTransform contentRoot,
            IUiWindowHandle handle, ManualLogSource log)
        {
            Id = id;
            DisplayName = displayName;
            Canvas = canvas;
            Font = font;
            WindowObject = windowObject;
            WindowRect = windowRect;
            ContentRoot = contentRoot;
            Handle = handle;
            Log = log;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public Canvas Canvas { get; }
        public TMP_FontAsset Font { get; }
        public GameObject WindowObject { get; }
        public RectTransform WindowRect { get; }
        public RectTransform ContentRoot { get; }
        public IUiWindowHandle Handle { get; }
        public ManualLogSource Log { get; }
    }

    public interface IUiWindowHandle
    {
        string Id { get; }
        bool IsRealized { get; }
        bool IsOpen { get; }
        UiWindowContext Context { get; }
        void Open();
        void Close();
        void Toggle();
        void BringToFront();
        void SetButtonStatus(UiButtonStatus status);
    }

    public struct UiButtonStatus
    {
        public bool DotVisible;
        public Color DotColor;
        public bool Blink;
        public float BlinkIntervalSeconds;
        public Color? BlinkOffColor;
        public string Text;
        public Color? TextColor;
    }

    internal sealed class UiWindowHandleImpl : IUiWindowHandle
    {
        private readonly UiWindowRegistration _registration;
        private readonly string _safeId;
        private ManualLogSource _log;
        private Canvas _canvas;
        private RectTransform _canvasRect;
        private ButtonVisualStyle _buttonStyle;
        private GameObject _buttonObject;
        private Image _buttonImage;
        private UiStatusDotPresenter _dotPresenter;
        private Component _buttonTooltip;
        private TextMeshProUGUI _statusText;
        private RectTransform _buttonRect;
        private GameObject _windowObject;
        private RectTransform _windowRect;
        private RectTransform _contentRoot;
        private UiWindowContext _context;
        private UiButtonStatus _status;
        private bool _requestedOpen;
        private bool _hovered;
        private bool _pressed;

        internal UiWindowHandleImpl(UiWindowRegistration registration)
        {
            _registration = registration;
            _safeId = SolarExpanseWindowManager.SanitizeId(registration.Id);
            _status = NormalizeStatus(default(UiButtonStatus));
        }

        internal UiWindowRegistration Registration => _registration;
        internal RectTransform ButtonRect => _buttonRect;
        internal int FocusOrder { get; private set; }
        internal Vector2 MinimumWindowSize => _registration.MinimumWindowSize;

        public string Id => _registration.Id;
        public bool IsRealized => _buttonObject != null && _windowObject != null && _context != null;
        public bool IsOpen => IsRealized ? _windowObject.activeSelf : _requestedOpen;
        public UiWindowContext Context => _context;

        internal void Realize(Canvas canvas, RectTransform canvasRect, Transform buttonParent,
            GameObject historyTemplate, TMP_FontAsset font, ButtonVisualStyle buttonStyle,
            ManualLogSource log)
        {
            _canvas = canvas;
            _canvasRect = canvasRect;
            _buttonStyle = buttonStyle;
            _log = log;

            CreateButton(buttonParent, font);
            CreateWindow(canvas, historyTemplate, font);
            ApplyStatus();

            if (_requestedOpen)
                Open();
        }

        public void Open()
        {
            _requestedOpen = true;
            if (!IsRealized)
                return;

            if (_windowObject.activeSelf)
            {
                BringToFront();
                return;
            }

            _windowObject.SetActive(true);
            _windowRect.anchorMin = new Vector2(0.5f, 0.5f);
            _windowRect.anchorMax = new Vector2(0.5f, 0.5f);
            _windowRect.pivot = new Vector2(0f, 1f);
            PlaceWindowBelowButton();
            BringToFront();
            UpdateButtonActiveState();

            try
            {
                _registration.OnOpen?.Invoke(_context);
            }
            catch (Exception e)
            {
                _log.LogError($"[SEWM] OnOpen exception for '{Id}': {e}");
            }
        }

        public void Close()
        {
            _requestedOpen = false;
            if (!IsRealized)
                return;

            if (!_windowObject.activeSelf)
            {
                UpdateButtonActiveState();
                return;
            }

            _windowObject.SetActive(false);
            UpdateButtonActiveState();

            try
            {
                _registration.OnClose?.Invoke(_context);
            }
            catch (Exception e)
            {
                _log.LogError($"[SEWM] OnClose exception for '{Id}': {e}");
            }
        }

        public void Toggle()
        {
            if (IsOpen)
                Close();
            else
                Open();
        }

        public void BringToFront()
        {
            if (!IsRealized)
                return;

            FocusOrder = SolarExpanseWindowManager.NextFocusOrder();
            _windowObject.transform.SetAsLastSibling();
            SolarExpanseWindowManager.KeepButtonGroupOnTop();
        }

        public void SetButtonStatus(UiButtonStatus status)
        {
            _status = NormalizeStatus(status);
            ApplyStatus();
        }

        internal void MoveOpenWindowBy(Vector2 movement)
        {
            if (!IsRealized || !_windowObject.activeSelf || movement.sqrMagnitude <= 0f)
                return;

            _windowRect.anchoredPosition += movement;
            ClampWindow();
        }

        internal void SetWindowSizeAndClamp(Vector2 size)
        {
            if (_windowRect == null)
                return;

            _windowRect.sizeDelta = new Vector2(
                Mathf.Max(_registration.MinimumWindowSize.x, size.x),
                Mathf.Max(_registration.MinimumWindowSize.y, size.y));
            ClampWindow();
        }

        internal void ClampWindow()
        {
            if (!IsRealized || !_windowObject.activeSelf || _canvasRect == null)
                return;

            Rect canvasRect = _canvasRect.rect;
            Vector2 size = _windowRect.sizeDelta;
            Vector2 pos = _windowRect.anchoredPosition;
            pos.x = SolarExpanseWindowManager.ClampEvenIfTooSmall(pos.x, canvasRect.xMin, canvasRect.xMax - size.x);
            pos.y = SolarExpanseWindowManager.ClampEvenIfTooSmall(pos.y, canvasRect.yMin + size.y, canvasRect.yMax);
            _windowRect.anchoredPosition = pos;
        }

        internal void DestroyRealization()
        {
            if (_windowObject != null)
                _requestedOpen = _windowObject.activeSelf;

            if (_buttonObject != null)
                UnityEngine.Object.Destroy(_buttonObject);
            if (_windowObject != null)
                UnityEngine.Object.Destroy(_windowObject);

            _buttonObject = null;
            _buttonImage = null;
            _dotPresenter = null;
            _buttonTooltip = null;
            _statusText = null;
            _buttonRect = null;
            _windowObject = null;
            _windowRect = null;
            _contentRoot = null;
            _context = null;
        }

        private void CreateButton(Transform parent, TMP_FontAsset font)
        {
            _buttonObject = new GameObject($"SEWM_Button_{_safeId}", typeof(RectTransform));
            _buttonObject.transform.SetParent(parent, false);
            _buttonRect = _buttonObject.GetComponent<RectTransform>();
            _buttonRect.sizeDelta = new Vector2(SolarExpanseWindowManager.ButtonSize, SolarExpanseWindowManager.ButtonSize);

            var layout = _buttonObject.AddComponent<LayoutElement>();
            layout.preferredWidth = SolarExpanseWindowManager.ButtonSize;
            layout.preferredHeight = SolarExpanseWindowManager.ButtonSize;
            layout.minWidth = SolarExpanseWindowManager.ButtonSize;
            layout.minHeight = SolarExpanseWindowManager.ButtonSize;
            layout.flexibleWidth = 0f;
            layout.flexibleHeight = 0f;

            _buttonImage = _buttonObject.AddComponent<Image>();
            _buttonImage.sprite = _buttonStyle.Sprite;
            _buttonImage.type = _buttonStyle.Sprite != null ? _buttonStyle.Type : Image.Type.Simple;
            _buttonImage.material = _buttonStyle.Material;
            _buttonImage.color = _buttonStyle.NormalColor;
            _buttonImage.raycastTarget = true;

            var input = _buttonObject.AddComponent<UiWindowButtonInput>();
            input.Handle = this;

            try
            {
                _buttonTooltip = SolarExpanseWindowManager.AddNativeTooltip(
                    _buttonObject, _registration.DisplayName);
                if (_buttonTooltip == null)
                    _log?.LogWarning($"[SEWM] Native hover label unavailable for '{Id}'");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[SEWM] Failed to configure hover label for '{Id}': {e.Message}");
            }

            GameObject iconGO = new GameObject("Icon", typeof(RectTransform));
            iconGO.transform.SetParent(_buttonObject.transform, false);
            RectTransform iconRT = iconGO.GetComponent<RectTransform>();
            iconRT.anchorMin = new Vector2(0.5f, 0.5f);
            iconRT.anchorMax = new Vector2(0.5f, 0.5f);
            iconRT.pivot = new Vector2(0.5f, 0.5f);
            iconRT.sizeDelta = new Vector2(28f, 28f);
            iconRT.anchoredPosition = new Vector2(0f, 2f);
            Image icon = iconGO.AddComponent<Image>();
            icon.sprite = SolarExpanseWindowManager.ResolveRegistrationIcon(_registration);
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            if (_registration.IconTint.HasValue)
                icon.color = _registration.IconTint.Value;

            GameObject dotGO = new GameObject("StatusDot", typeof(RectTransform));
            dotGO.transform.SetParent(_buttonObject.transform, false);
            RectTransform dotRT = dotGO.GetComponent<RectTransform>();
            dotRT.anchorMin = new Vector2(0f, 1f);
            dotRT.anchorMax = new Vector2(0f, 1f);
            dotRT.pivot = new Vector2(0.5f, 0.5f);
            dotRT.sizeDelta = new Vector2(14f, 14f);
            dotRT.anchoredPosition = new Vector2(8f, -8f);
            TextMeshProUGUI dotLabel = dotGO.AddComponent<TextMeshProUGUI>();
            if (font != null)
                dotLabel.font = font;
            dotLabel.text = "●";
            dotLabel.fontSize = 11f;
            dotLabel.enableWordWrapping = false;
            dotLabel.alignment = TextAlignmentOptions.Center;
            dotLabel.raycastTarget = false;
            _dotPresenter = dotGO.AddComponent<UiStatusDotPresenter>();
            _dotPresenter.Label = dotLabel;

            GameObject textGO = new GameObject("StatusText", typeof(RectTransform));
            textGO.transform.SetParent(_buttonObject.transform, false);
            RectTransform textRT = textGO.GetComponent<RectTransform>();
            textRT.anchorMin = new Vector2(0f, 0f);
            textRT.anchorMax = new Vector2(1f, 0f);
            textRT.pivot = new Vector2(0.5f, 0f);
            textRT.offsetMin = new Vector2(3f, 3f);
            textRT.offsetMax = new Vector2(-3f, 15f);
            _statusText = textGO.AddComponent<TextMeshProUGUI>();
            if (font != null)
                _statusText.font = font;
            _statusText.fontSize = 9f;
            _statusText.enableAutoSizing = true;
            _statusText.fontSizeMin = 6f;
            _statusText.fontSizeMax = 9f;
            _statusText.alignment = TextAlignmentOptions.BottomRight;
            _statusText.enableWordWrapping = false;
            _statusText.overflowMode = TextOverflowModes.Ellipsis;
            _statusText.raycastTarget = false;

            UpdateButtonActiveState();
        }

        private void CreateWindow(Canvas canvas, GameObject historyTemplate, TMP_FontAsset font)
        {
            _windowObject = UnityEngine.Object.Instantiate(historyTemplate, canvas.transform);
            _windowObject.name = $"SEWM_Window_{_safeId}";
            _windowObject.SetActive(true);
            _windowObject.transform.SetAsLastSibling();

            var layoutElement = _windowObject.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;

            _windowRect = _windowObject.GetComponent<RectTransform>();
            _windowRect.anchorMin = new Vector2(0.5f, 0.5f);
            _windowRect.anchorMax = new Vector2(0.5f, 0.5f);
            _windowRect.pivot = new Vector2(0f, 1f);
            _windowRect.sizeDelta = _registration.DefaultWindowSize;
            _windowRect.anchoredPosition = new Vector2(-9999f, -9999f);

            Image bgSource = null;
            foreach (Image img in _windowObject.GetComponentsInChildren<Image>(includeInactive: true))
            {
                if (img.sprite != null)
                {
                    bgSource = img;
                    break;
                }
            }

            Image panelBg = _windowObject.GetComponent<Image>() ?? _windowObject.AddComponent<Image>();
            if (bgSource != null)
            {
                panelBg.sprite = bgSource.sprite;
                panelBg.color = bgSource.color;
                panelBg.type = bgSource.type;
                panelBg.material = bgSource.material;
            }
            else
            {
                panelBg.color = new Color(0.07f, 0.08f, 0.10f, 0.96f);
            }
            panelBg.raycastTarget = true;

            for (int i = _windowObject.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(_windowObject.transform.GetChild(i).gameObject);

            foreach (ScrollRect scrollRect in _windowObject.GetComponents<ScrollRect>())
                UnityEngine.Object.DestroyImmediate(scrollRect);
            foreach (LayoutGroup layout in _windowObject.GetComponents<LayoutGroup>())
                UnityEngine.Object.DestroyImmediate(layout);
            ContentSizeFitter fitter = _windowObject.GetComponent<ContentSizeFitter>();
            if (fitter != null)
                UnityEngine.Object.DestroyImmediate(fitter);

            CanvasGroup canvasGroup = _windowObject.GetComponent<CanvasGroup>() ??
                _windowObject.AddComponent<CanvasGroup>();
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;

            var focusHandler = _windowObject.AddComponent<UiWindowFocusHandler>();
            focusHandler.Handle = this;

            GameObject contentGO = new GameObject("ContentRoot", typeof(RectTransform));
            contentGO.transform.SetParent(_windowObject.transform, false);
            _contentRoot = contentGO.GetComponent<RectTransform>();
            _contentRoot.anchorMin = Vector2.zero;
            _contentRoot.anchorMax = Vector2.one;
            _contentRoot.pivot = new Vector2(0.5f, 0.5f);
            _contentRoot.offsetMin = new Vector2(8f, 18f);
            _contentRoot.offsetMax = new Vector2(-8f, -8f);

            GameObject resizeGO = new GameObject("ResizeHandle", typeof(RectTransform));
            resizeGO.transform.SetParent(_windowObject.transform, false);
            RectTransform resizeRT = resizeGO.GetComponent<RectTransform>();
            resizeRT.anchorMin = new Vector2(0f, 0f);
            resizeRT.anchorMax = new Vector2(1f, 0f);
            resizeRT.pivot = new Vector2(0.5f, 1f);
            resizeRT.sizeDelta = new Vector2(0f, 10f);
            resizeRT.anchoredPosition = Vector2.zero;
            resizeGO.AddComponent<Image>().color = Color.clear;
            resizeGO.AddComponent<UiWindowResizeHandle>().Handle = this;

            _context = new UiWindowContext(Id, _registration.DisplayName, canvas, font,
                _windowObject, _windowRect, _contentRoot, this, _log);

            try
            {
                _registration.BuildContent(_context);
            }
            catch (Exception e)
            {
                _log.LogError($"[SEWM] BuildContent exception for '{Id}': {e}");
            }

            _windowObject.SetActive(false);
        }

        private void PlaceWindowBelowButton()
        {
            if (_buttonRect == null || _windowRect == null)
                return;

            Vector3[] corners = new Vector3[4];
            _buttonRect.GetWorldCorners(corners);
            Vector2 buttonTopLeft = SolarExpanseWindowManager.CanvasLocalPointFromWorld(corners[1]);
            Vector2 buttonBottomLeft = SolarExpanseWindowManager.CanvasLocalPointFromWorld(corners[0]);
            _windowRect.anchoredPosition = new Vector2(
                buttonTopLeft.x,
                buttonBottomLeft.y - SolarExpanseWindowManager.WindowDropOffset);
            ClampWindow();
        }

        private void ApplyStatus()
        {
            if (!IsRealized)
                return;

            _dotPresenter?.SetStatus(_status);

            if (_statusText != null)
            {
                bool hasText = !string.IsNullOrEmpty(_status.Text);
                _statusText.gameObject.SetActive(hasText);
                _statusText.text = _status.Text ?? string.Empty;
                _statusText.color = _status.TextColor ?? SolarExpanseWindowManager.MutedStatusTextColor;
            }
        }

        private void UpdateButtonActiveState()
        {
            if (_buttonImage == null)
                return;

            if (IsOpen)
            {
                _buttonImage.sprite = _buttonStyle.ActiveSprite ?? _buttonStyle.Sprite;
                _buttonImage.color = _buttonStyle.ActiveColor;
            }
            else
            {
                _buttonImage.sprite = _buttonStyle.Sprite;
                _buttonImage.color = _pressed
                    ? _buttonStyle.PressedColor
                    : _hovered ? _buttonStyle.HoverColor : _buttonStyle.NormalColor;
            }
        }

        internal void SetPointerState(bool hovered, bool pressed)
        {
            _hovered = hovered;
            _pressed = pressed;
            UpdateButtonActiveState();
        }

        private static UiButtonStatus NormalizeStatus(UiButtonStatus status)
        {
            if (status.DotColor == default(Color))
                status.DotColor = Color.white;
            if (status.BlinkIntervalSeconds <= 0f)
                status.BlinkIntervalSeconds = 0.5f;
            return status;
        }

    }

    internal sealed class UiStatusDotPresenter : MonoBehaviour
    {
        internal TextMeshProUGUI Label;

        private UiButtonStatus _status;
        private float _blinkTimer;
        private bool _blinkOn = true;

        internal void SetStatus(UiButtonStatus status)
        {
            if (BlinkAppearanceChanged(_status, status))
            {
                _blinkTimer = 0f;
                _blinkOn = true;
            }

            _status = status;
            Apply();
        }

        private void Update()
        {
            if (Label == null || !_status.DotVisible || !_status.Blink)
                return;

            _blinkTimer += Time.unscaledDeltaTime;
            float interval = _status.BlinkIntervalSeconds <= 0f ? 0.5f : _status.BlinkIntervalSeconds;
            while (_blinkTimer >= interval)
            {
                _blinkTimer -= interval;
                _blinkOn = !_blinkOn;
            }

            Label.color = CurrentColor();
        }

        private void Apply()
        {
            if (Label == null)
                return;

            Label.gameObject.SetActive(_status.DotVisible);
            Label.color = CurrentColor();
        }

        private Color CurrentColor()
        {
            if (!_status.Blink || _blinkOn)
                return _status.DotColor;
            if (_status.BlinkOffColor.HasValue)
                return _status.BlinkOffColor.Value;
            return new Color(_status.DotColor.r * 0.08f, _status.DotColor.g * 0.08f,
                _status.DotColor.b * 0.08f, Mathf.Min(_status.DotColor.a, 0.25f));
        }

        private static bool BlinkAppearanceChanged(UiButtonStatus current, UiButtonStatus next)
        {
            return current.DotVisible != next.DotVisible ||
                current.Blink != next.Blink ||
                current.DotColor != next.DotColor ||
                !Mathf.Approximately(current.BlinkIntervalSeconds, next.BlinkIntervalSeconds) ||
                !Nullable.Equals(current.BlinkOffColor, next.BlinkOffColor);
        }
    }

    internal sealed class ButtonVisualStyle
    {
        public Sprite Sprite;
        public Sprite ActiveSprite;
        public Sprite GroupSprite;
        public Image.Type Type;
        public Material Material;
        public Color NormalColor;
        public Color HoverColor;
        public Color PressedColor;
        public Color ActiveColor;
    }

    internal sealed class UiWindowFocusHandler : MonoBehaviour, IPointerDownHandler
    {
        internal UiWindowHandleImpl Handle;

        public void OnPointerDown(PointerEventData eventData) => Handle?.BringToFront();
    }

    internal sealed class UiButtonGroupViewportGuard : MonoBehaviour
    {
        private void OnEnable() => Canvas.willRenderCanvases += BeforeCanvasRender;

        private void OnDisable() => Canvas.willRenderCanvases -= BeforeCanvasRender;

        private void LateUpdate() => SolarExpanseWindowManager.EnsureButtonGroupVisible();

        private static void BeforeCanvasRender() => SolarExpanseWindowManager.EnsureButtonGroupVisible();
    }

    internal sealed class UiButtonGroupMover : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IDragHandler, IEndDragHandler
    {
        private RectTransform _rectTransform;
        private Canvas _canvas;
        private Vector2 _pressScreen;
        private Vector2 _dragStartScreen;
        private Vector2 _dragStartPosition;
        private bool _pressed;
        private bool _dragging;
        private bool _dragConsumed;

        private void Awake()
        {
            _rectTransform = GetComponent<RectTransform>();
            _canvas = GetComponentInParent<Canvas>();
        }

        public void OnPointerDown(PointerEventData eventData) => BeginPress(eventData.position);

        public void OnPointerUp(PointerEventData eventData) => EndPress(eventData.position);

        public void OnDrag(PointerEventData eventData) => DragTo(eventData.position);

        public void OnEndDrag(PointerEventData eventData) => EndPress(eventData.position);

        internal void BeginPress(Vector2 screenPosition)
        {
            if (_rectTransform == null)
                _rectTransform = GetComponent<RectTransform>();
            if (_canvas == null)
                _canvas = GetComponentInParent<Canvas>();

            _pressed = true;
            _dragging = false;
            _dragConsumed = false;
            _pressScreen = screenPosition;
            _dragStartScreen = screenPosition;
            _dragStartPosition = _rectTransform != null
                ? _rectTransform.anchoredPosition
                : Vector2.zero;
        }

        internal void DragTo(Vector2 screenPosition)
        {
            if (!_pressed || _rectTransform == null)
                return;

            int threshold = EventSystem.current != null ? EventSystem.current.pixelDragThreshold : 5;
            if (!_dragging && Vector2.Distance(screenPosition, _pressScreen) < threshold)
                return;

            _dragging = true;
            _dragConsumed = true;
            float scale = _canvas != null ? _canvas.scaleFactor : 1f;
            Vector2 delta = (screenPosition - _dragStartScreen) / scale;
            SolarExpanseWindowManager.MoveButtonGroup(_dragStartPosition + delta, storeUserPosition: true);
        }

        internal bool EndPress(Vector2 screenPosition)
        {
            bool wasClick = _pressed && !_dragConsumed;
            _pressed = false;
            _dragging = false;
            _dragConsumed = false;
            return wasClick;
        }
    }

    internal sealed class UiWindowButtonInput : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler,
        IDragHandler, IEndDragHandler
    {
        internal UiWindowHandleImpl Handle;

        private UiButtonGroupMover _groupMover;
        private bool _hovered;

        private UiButtonGroupMover GroupMover
        {
            get
            {
                if (_groupMover == null)
                    _groupMover = GetComponentInParent<UiButtonGroupMover>();
                return _groupMover;
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovered = true;
            Handle?.SetPointerState(hovered: true, pressed: false);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            Handle?.SetPointerState(hovered: false, pressed: false);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            GroupMover?.BeginPress(eventData.position);
            Handle?.SetPointerState(_hovered, pressed: true);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            bool click = GroupMover == null || GroupMover.EndPress(eventData.position);
            Handle?.SetPointerState(_hovered, pressed: false);
            if (click)
                Handle?.Toggle();
        }

        public void OnDrag(PointerEventData eventData)
        {
            GroupMover?.DragTo(eventData.position);
            Handle?.SetPointerState(_hovered, pressed: false);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            GroupMover?.EndPress(eventData.position);
            Handle?.SetPointerState(_hovered, pressed: false);
        }
    }

    internal sealed class UiWindowResizeHandle : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        internal UiWindowHandleImpl Handle;

        private static Texture2D _cursor;
        private Canvas _canvas;
        private bool _dragging;
        private Vector2 _dragStartScreen;
        private Vector2 _dragStartSize;

        private void Awake()
        {
            _canvas = GetComponentInParent<Canvas>();
            if (_cursor == null)
                _cursor = BuildCursor();
        }

        public void OnPointerEnter(PointerEventData eventData) =>
            Cursor.SetCursor(_cursor, new Vector2(16f, 16f), CursorMode.Auto);

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!_dragging)
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _dragging = true;
            _dragStartScreen = eventData.position;
            _dragStartSize = Handle != null && Handle.Context != null && Handle.Context.WindowRect != null
                ? Handle.Context.WindowRect.sizeDelta
                : Vector2.zero;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _dragging = false;
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Handle == null)
                return;

            float scale = _canvas != null ? _canvas.scaleFactor : 1f;
            Vector2 delta = (eventData.position - _dragStartScreen) / scale;
            Vector2 size = new Vector2(
                _dragStartSize.x + delta.x,
                _dragStartSize.y - delta.y);
            Handle.SetWindowSizeAndClamp(size);
        }

        private static Texture2D BuildCursor()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.clear;

            void Dot(int x, int y, Color c)
            {
                if (x >= 0 && x < size && y >= 0 && y < size)
                    pixels[y * size + x] = c;
            }

            for (int i = 8; i < 24; i++)
            {
                Dot(i, size - 1 - i, Color.black);
                Dot(i + 1, size - 1 - i, Color.black);
                Dot(i, size - i, Color.black);
                Dot(i, size - 2 - i, Color.white);
            }

            for (int i = 0; i < 6; i++)
            {
                Dot(23 - i, 9 + i, Color.white);
                Dot(23 - i, 10 + i, Color.white);
                Dot(8 + i, 24 - i, Color.white);
                Dot(9 + i, 24 - i, Color.white);
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }

    internal static class PauseScreenEscPatch
    {
        private static int _suppressFrame = -1;
        private static MonoBehaviour _pauseScreenInstance;
        private static MethodInfo _closeMethod;
        private static Type _pauseScreenType;

        internal static void Apply(Harmony harmony, ManualLogSource log)
        {
            _pauseScreenType = AccessTools.TypeByName("Game.UI.Screens.PauseScreen");
            if (_pauseScreenType == null)
            {
                log.LogWarning("[SEWM] PauseScreen type not found; ESC window close disabled");
                return;
            }

            Type baseScreenType = AccessTools.TypeByName("Game.UI.Screens.BaseScreen");
            if (baseScreenType != null)
            {
                MethodInfo setVisible = AccessTools.PropertySetter(baseScreenType, "Visible");
                if (setVisible != null)
                    harmony.Patch(setVisible,
                        prefix: new HarmonyMethod(typeof(PauseScreenEscPatch), nameof(VisibleSetPrefix)));
            }

            var prefix = new HarmonyMethod(typeof(PauseScreenEscPatch), nameof(Prefix));
            int count = 0;
            foreach (string name in new[] { "CustomUpdate", "Update", "Open", "Show", "Toggle" })
            {
                MethodInfo method = AccessTools.Method(_pauseScreenType, name);
                if (method == null)
                    continue;
                harmony.Patch(method, prefix: prefix);
                count++;
            }

            if (count == 0)
                log.LogWarning("[SEWM] PauseScreen fallback methods not found; ESC fallback disabled");

            foreach (string name in new[] { "Awake", "Start" })
            {
                MethodInfo method = AccessTools.Method(_pauseScreenType, name);
                if (method == null)
                    continue;
                harmony.Patch(method,
                    postfix: new HarmonyMethod(typeof(PauseScreenEscPatch), nameof(CapturePostfix)));
                break;
            }
        }

        private static bool VisibleSetPrefix(object __instance, bool value)
        {
            if (value && _pauseScreenType != null && _pauseScreenType.IsInstanceOfType(__instance) &&
                SolarExpanseWindowManager.CloseTopmostWindow())
            {
                _suppressFrame = Time.frameCount;
                return false;
            }

            return true;
        }

        private static void CapturePostfix(MonoBehaviour __instance)
        {
            _pauseScreenInstance = __instance;
            Type t = __instance.GetType();
            while (t != null && t != typeof(MonoBehaviour))
            {
                PropertyInfo prop = t.GetProperty("Visible",
                    BindingFlags.Instance | BindingFlags.Public);
                if (prop?.SetMethod != null)
                {
                    _closeMethod = prop.SetMethod;
                    break;
                }
                t = t.BaseType;
            }
        }

        private static bool Prefix()
        {
            if (_suppressFrame == Time.frameCount)
                return false;

            if (SolarExpanseWindowManager.CloseTopmostWindow())
            {
                _suppressFrame = Time.frameCount;
                return false;
            }

            return true;
        }

        internal static void LateUpdateTick()
        {
            if (_suppressFrame != Time.frameCount)
                return;
            if (_pauseScreenInstance == null || !_pauseScreenInstance.gameObject.activeSelf)
                return;

            if (_closeMethod != null)
                _closeMethod.Invoke(_pauseScreenInstance, new object[] { false });
            else
                _pauseScreenInstance.gameObject.SetActive(false);
        }
    }
}
