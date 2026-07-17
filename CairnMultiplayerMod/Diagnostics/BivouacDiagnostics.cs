using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace CairnMultiplayerMod.Diagnostics;

internal static unsafe partial class BivouacDiagnostics
{
    private static MonoBehaviour _tapingFingersManagerCached;
    private static int _lastTapingFingersManagerSearchFrame;

    /// <summary>Forgets the scene-bound TapingFingersManager reference (called on scene reload).</summary>
    internal static void ResetCaches()
    {
        _tapingFingersManagerCached = null;
        _lastTapingFingersManagerSearchFrame = 0;
    }

    internal static string BuildBivouacDebugSnapshot()
    {
        var lifecycle = "lifecycle=unavailable";
        if (GameLifecycleService.TryGetGameLifecycle(out var lifecycleState, out var lifecycleDetail))
            lifecycle = $"lifecycle={lifecycleState} ({lifecycleDetail})";

        return $"{lifecycle}; {BuildBivouacManagerDebug()}; {BuildTapingFingersDebug()}; " +
               $"{BuildInputManagerDebug()}; {BuildEventSystemDebug()}; {BuildToolkitUiDebug()}; {BuildLocalPawnDebug()}";
    }

    private static string BuildBivouacManagerDebug()
    {
        try
        {
            var manager = GameLifecycleService.FindBivouacManager();
            if (manager == null)
                return "bivouacManager=missing";

            return "bivouacManager=" +
                   $"in={manager.IsInBivouac} transition={manager.IsInTransition} " +
                   $"preview={manager.IsInPreview} area={manager.IsInBivouacArea} " +
                   $"onWall={manager.IsOnWall} resource={manager.HasResourceNearby}";
        }
        catch (Exception ex)
        {
            return $"bivouacManager=error:{ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}";
        }
    }

    private static string BuildTapingFingersDebug()
    {
        try
        {
            var taping = FindTapingFingersManager();
            if (taping == null)
                return "taping=missing";

            var visuals = taping.visualsParent;
            var arms = taping.armsOffsetForTransition;
            return "taping=" +
                   $"open={taping.IsOpen} isTaping={taping.IsTaping} instant={taping.isInstantTaping} " +
                   $"progress={taping.tapingProgress:0.000} buffer={taping.tapeBuffer:0.000} " +
                   $"finger={taping.currentFinger} hand={taping.currentHand} selectedHand={taping.HasSelectedHand} " +
                   $"allowed={taping.IsAllowedToTape} reveal={taping.CanRevealHandInfo} mustHide={taping.MustHide} " +
                   $"visuals={DescribeTransform(visuals)} arms={DescribeTransform(arms)} " +
                   $"group={DescribeNull(taping.group)} tapeAnimator={DescribeNull(taping.tapeAnimator)} " +
                   $"tapeRenderer={DescribeNull(taping.tapeRenderer)} currentModel={DescribeNull(GetCurrentModelSafe(taping))} " +
                   $"inputHandle={DescribeNull(taping.inputHandle)} prompts={DescribeNull(taping.inputPromptGroup)} " +
                   $"stick={DescribeNull(taping.stickGroup)} bandage={DescribeNull(taping.bandageCanvasGroup)} " +
                   $"pushNearClip={DescribeNull(taping.pushNearClip)} fingerStates={DescribeNull(taping.fingersGameStates)} " +
                   $"models={DescribeNull(taping.models)} parameters={DescribeNull(taping.tapingParametersPerFinger)}";
        }
        catch (Exception ex)
        {
            return $"taping=error:{ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}";
        }
    }

    private static string BuildLocalPawnDebug()
    {
        try
        {
            var mc = LocalPlayerApi.TryGetLocalMCGameObject();
            if (mc == null)
                return "mc=missing";

            var t = mc.transform;
            var p = t.position;
            return "mc=" +
                   $"name='{mc.name}' activeSelf={mc.activeSelf} activeInHierarchy={mc.activeInHierarchy} " +
                   $"pos=({p.x:0.0},{p.y:0.0},{p.z:0.0})";
        }
        catch (Exception ex)
        {
            return $"mc=error:{ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}";
        }
    }

    private static TapingFingersManager FindTapingFingersManager()
    {
        var comp = GameInterop.FindMonoBehaviourByName("TapingFingersManager", ref _tapingFingersManagerCached,
            ref _lastTapingFingersManagerSearchFrame);
        return comp?.TryCast<TapingFingersManager>();
    }

    private static string BuildInputManagerDebug()
    {
        try
        {
            var manager = InputApi.FindInputManager();
            if (manager == null)
                return "input=missing";

            var stack = manager.GetInputContextStack();
            var stackText = stack == null ? "null" : $"count={stack.Count}";
            return "input=" +
                   $"context={manager.CurrentInputContext} mouse={manager.CurrentInputContextUsesMouse} " +
                   $"disabled={manager.disableInputs} stack={stackText} " +
                   $"fingerMap={DescribeMap(manager.fingersTapingMap)} " +
                   $"gameplayUiMap={DescribeMap(manager.gameplayUIActionMap)} " +
                   $"mainMenuMap={DescribeMap(manager.mainMenuUIMap)}";
        }
        catch (Exception ex)
        {
            return $"input=error:{ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}";
        }
    }

    private static string BuildEventSystemDebug()
    {
        try
        {
            var es = EventSystem.current;
            if (es == null)
                return "eventSystem=missing";

            var selected = es.currentSelectedGameObject;
            var module = es.currentInputModule;
            var topRaycast = "none";
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var data = new PointerEventData(es) { position = mouse.position.ReadValue() };
                var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                es.RaycastAll(data, hits);
                if (hits.Count > 0)
                    topRaycast = DescribeGameObject(hits[0].gameObject);
            }

            return "eventSystem=" +
                   $"selected={DescribeGameObject(selected)} module={DescribeObject(module)} topRaycast={topRaycast}";
        }
        catch (Exception ex)
        {
            return $"eventSystem=error:{ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}";
        }
    }

    private static string BuildToolkitUiDebug()
    {
        try
        {
            var go = GameObject.Find("MP_UIToolkit");
            if (go == null)
                return "mpUiToolkit=absent";

            var doc = go.GetComponent<UIDocument>();
            var root = doc?.rootVisualElement;
            return "mpUiToolkit=" +
                   $"activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy} " +
                   $"document={DescribeBehaviour(doc)} rootChildren={(root == null ? -1 : root.childCount)} " +
                   $"rootDisplay={(root == null ? "null" : root.style.display.value.ToString())} " +
                   $"rootPicking={(root == null ? "null" : root.pickingMode.ToString())}";
        }
        catch (Exception ex)
        {
            return $"mpUiToolkit=error:{ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}";
        }
    }

    private static string DescribeTransform(Transform transform)
    {
        if (transform == null)
            return "null";

        try
        {
            var go = transform.gameObject;
            return $"'{go.name}' active={go.activeInHierarchy}";
        }
        catch
        {
            return "error";
        }
    }

    private static string DescribeMap(UnityEngine.InputSystem.InputActionMap map)
    {
        if (map == null)
            return "null";

        try
        {
            return $"{map.name}:enabled={map.enabled}";
        }
        catch
        {
            return "error";
        }
    }

    private static string DescribeGameObject(GameObject go)
    {
        if (go == null)
            return "null";

        try
        {
            return $"'{go.name}'";
        }
        catch
        {
            return "error";
        }
    }

    private static string DescribeObject(UnityEngine.Object obj)
    {
        if (obj == null)
            return "null";

        try
        {
            return $"'{obj.name}'";
        }
        catch
        {
            return "error";
        }
    }

    private static string DescribeBehaviour(Behaviour behaviour)
    {
        if (behaviour == null)
            return "null";

        try
        {
            return $"enabled={behaviour.enabled}";
        }
        catch
        {
            return "error";
        }
    }

    private static string DescribeNull(object value)
    {
        return value == null ? "null" : "ok";
    }

    private static object GetCurrentModelSafe(TapingFingersManager taping)
    {
        try
        {
            return taping?.CurrentModel;
        }
        catch
        {
            return null;
        }
    }
}
