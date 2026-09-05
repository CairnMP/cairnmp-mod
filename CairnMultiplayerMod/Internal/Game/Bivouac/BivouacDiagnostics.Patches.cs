using CairnMultiplayerMod.Internal.Diagnostics;
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Bivouac;

internal static partial class BivouacDiagnostics
{
    // Keep the historical id so reloads can unpatch hooks installed by older builds.
    private static readonly HarmonyLib.Harmony BivouacSafetyHarmony = new("CairnMultiplayerMod.BivouacDiagnostics");
    private static bool _bivouacDiagnosticsPatchesInstalled;
    private static bool _bivouacDiagnosticsPatchesFailed;
    private static readonly HashSet<IntPtr> TapingInitAttempted = new();
    private static readonly HashSet<string> KeepLogEvent = new(StringComparer.Ordinal)
    {
        "TapingActivate",
        "TapingActivateBypass",
        "TapingPrepareSelection",
        "TapingActivateException",
        "TapingActivationCleanup",
        "TapingStartBypass",
        "TapingStartBypassFailed",
        "TapingCurrentModelFailed"
    };

    internal static void Install()
    {
        if (_bivouacDiagnosticsPatchesInstalled || _bivouacDiagnosticsPatchesFailed)
            return;

        try
        {
            Patch(typeof(BivouacInteractiveObject), nameof(BivouacInteractiveObject.PointerHitbox_OnPointerClickCallback),
                nameof(BivouacDiagnosticPatchMethods.BivouacPointerClickPrefix));
            Patch(typeof(BivouacObjectOption), nameof(BivouacObjectOption.Execute),
                nameof(BivouacDiagnosticPatchMethods.BivouacOptionExecutePrefix));
            Patch(typeof(BivouacMenu), nameof(BivouacMenu.OnTapingFingersAction),
                nameof(BivouacDiagnosticPatchMethods.TapingFingersActionPrefix));
            Patch(typeof(TapingFingersManager), nameof(TapingFingersManager.Activate),
                nameof(BivouacDiagnosticPatchMethods.TapingActivatePrefix), new[] { typeof(Il2CppSystem.Action) },
                nameof(BivouacDiagnosticPatchMethods.TapingActivateFinalizer));
            Patch(typeof(TapingFingersManager), nameof(TapingFingersManager.Deactivate),
                nameof(BivouacDiagnosticPatchMethods.TapingDeactivatePrefix));
            Patch(typeof(TapingFingersManager), nameof(TapingFingersManager.StartTaping),
                nameof(BivouacDiagnosticPatchMethods.TapingStartPrefix));

            _bivouacDiagnosticsPatchesInstalled = true;
            ModLog.Debug("[BivouacDebug] Diagnostic patches installed");
        }
        catch (Exception ex)
        {
            _bivouacDiagnosticsPatchesFailed = true;
            ModLog.Warning($"[BivouacDebug] Diagnostic patch install failed: {ex.Message}");
        }
    }

    internal static void Uninstall()
    {
        try { BivouacSafetyHarmony.UnpatchSelf(); }
        finally
        {
            _bivouacDiagnosticsPatchesInstalled = false;
            _bivouacDiagnosticsPatchesFailed = false;
            TapingInitAttempted.Clear();
            ResetCaches();
        }
    }

    private static void Patch(Type targetType, string targetMethodName, string prefixMethodName, Type[] parameters = null,
        string finalizerMethodName = null)
    {
        var original = parameters == null
            ? AccessTools.Method(targetType, targetMethodName)
            : AccessTools.Method(targetType, targetMethodName, parameters);
        var prefix = AccessTools.Method(typeof(BivouacDiagnosticPatchMethods), prefixMethodName);
        var finalizer = finalizerMethodName == null
            ? null
            : AccessTools.Method(typeof(BivouacDiagnosticPatchMethods), finalizerMethodName);

        if (original == null || prefix == null || (finalizerMethodName != null && finalizer == null))
            throw new MissingMethodException($"{targetType.Name}.{targetMethodName} or {prefixMethodName}");

        BivouacSafetyHarmony.Patch(original, prefix: new HarmonyMethod(prefix),
            finalizer: finalizer == null ? null : new HarmonyMethod(finalizer));
    }

    private static class BivouacDiagnosticPatchMethods
    {
        internal static void BivouacPointerClickPrefix(BivouacInteractiveObject __instance, PointerEventsListener obj)
        {
            SafeLog("PointerClick", () =>
                $"object={DescribeBivouacObject(__instance)} pointer={DescribeBehaviour(obj)}");
        }

        internal static void BivouacOptionExecutePrefix(BivouacObjectOption __instance)
        {
            SafeLog("OptionExecute", () =>
                $"option={DescribeBehaviour(__instance)} available={(__instance != null && __instance.Available)}");
        }

        internal static void TapingFingersActionPrefix(BivouacMenu __instance)
        {
            SafeLog("TapingAction", () =>
                $"current={DescribeBivouacObject(__instance?.currentInteractiveObject)} hidden={(__instance != null && __instance.isInHiddenMode)}");
        }

        internal static bool TapingActivatePrefix(TapingFingersManager __instance)
        {
            // Diagnostic only: log the state before the game runs its Activate.
            // The prefix must NOT short-circuit the vanilla flow (camera, input
            // context, animations), otherwise the character disappears and the
            // game locks up.
            SafeLog("TapingActivate", () => DescribeTaping(__instance));

            // Repair the preconditions just in case (hand selection + current
            // model) without calling StartTaping ourselves: the original Activate
            // is what drives the rest.
            if (EnsureTapingManagerReady(__instance))
            {
                PrepareTapingSelection(__instance);
                _ = EnsureCurrentModel(__instance);
            }
            else
            {
                SafeLog("TapingActivateSkipped", () => DescribeTapingReadiness(__instance));
            }

            return true;
        }

        internal static Exception TapingActivateFinalizer(TapingFingersManager __instance, Il2CppSystem.Action onExitCallback,
            Exception __exception)
        {
            if (__exception == null)
                return null;

            SafeLog("TapingActivateException", () =>
                $"{__exception.GetType().Name}:{GameInterop.FirstLine(__exception.Message)} readiness={DescribeTapingReadiness(__instance)}");
            RecoverFromFailedActivation(__instance, onExitCallback, "TapingActivateException");
            return null;
        }

        internal static void TapingDeactivatePrefix(TapingFingersManager __instance)
        {
            SafeLog("TapingDeactivate", () => DescribeTaping(__instance));
        }

        internal static void TapingStartPrefix(TapingFingersManager __instance)
        {
            SafeLog("TapingStart", () => DescribeTaping(__instance));
        }

        private static void SafeLog(string eventName, Func<string> build)
        {
            if (!ShouldLogEvent(eventName))
                return;

            try
            {
                ModLog.Debug($"[BivouacDebug] event={eventName} {build()}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[BivouacDebug] event={eventName} log failed: {ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}");
            }
        }

        private static string DescribeBivouacObject(BivouacInteractiveObject obj)
        {
            if (obj == null)
                return "null";

            return $"'{obj.name}' type={obj.objectType} showing={obj.ShowingUI} interactable={obj.IsInteractable()}";
        }

        private static string DescribeTaping(TapingFingersManager manager)
        {
            if (manager == null)
                return "manager=null";

            return $"open={manager.IsOpen} isTaping={manager.IsTaping} hand={manager.currentHand} " +
                   $"finger={manager.currentFinger} allowed={manager.IsAllowedToTape} " +
                   $"visuals={DescribeTransform(manager.visualsParent)} arms={DescribeTransform(manager.armsOffsetForTransition)} " +
                   DescribeTapingReadiness(manager);
        }

        private static bool EnsureTapingManagerReady(TapingFingersManager manager)
        {
            if (manager == null || manager.Pointer == IntPtr.Zero)
                return false;

            if (HasCriticalTapingReferences(manager))
                return true;

            if (TapingInitAttempted.Contains(manager.Pointer))
                return HasCriticalTapingReferences(manager);

            TapingInitAttempted.Add(manager.Pointer);
            SafeLog("TapingInitRepair", () => $"before={DescribeTapingReadiness(manager)}");
            TryInvokePrivate(manager, "Init");
            SafeLog("TapingInitRepair", () => $"afterInit={DescribeTapingReadiness(manager)}");

            if (HasCriticalTapingReferences(manager))
                return true;

            TryInvokePrivate(manager, "FakeInit");
            SafeLog("TapingInitRepair", () => $"afterFakeInit={DescribeTapingReadiness(manager)}");
            return HasCriticalTapingReferences(manager);
        }

        private static bool HasCriticalTapingReferences(TapingFingersManager manager)
        {
            return manager != null
                   && manager.group != null
                   && manager.visualsParent != null
                   && manager.tapeAnimator != null
                   && manager.tapeRenderer != null
                   && manager.armsOffsetForTransition != null
                   && manager.fingersGameStates != null
                   && manager.models != null
                   && manager.tapingParametersPerFinger != null;
        }

        private static string DescribeTapingReadiness(TapingFingersManager manager)
        {
            if (manager == null)
                return "ready=False manager=null";

            return "ready=" + HasCriticalTapingReferences(manager) +
                   $" group={DescribeNull(manager.group)}" +
                   $" visuals={DescribeNull(manager.visualsParent)}" +
                   $" tapeAnimator={DescribeNull(manager.tapeAnimator)}" +
                   $" tapeRenderer={DescribeNull(manager.tapeRenderer)}" +
                   $" arms={DescribeNull(manager.armsOffsetForTransition)}" +
                   $" currentModel={DescribeNull(GetCurrentModelSafe(manager))}" +
                   $" inputHandle={DescribeNull(manager.inputHandle)}" +
                   $" prompts={DescribeNull(manager.inputPromptGroup)}" +
                   $" stick={DescribeNull(manager.stickGroup)}" +
                   $" bandage={DescribeNull(manager.bandageCanvasGroup)}" +
                   $" pushNearClip={DescribeNull(manager.pushNearClip)}" +
                   $" fingerStates={DescribeNull(manager.fingersGameStates)}" +
                   $" models={DescribeNull(manager.models)}" +
                   $" parameters={DescribeNull(manager.tapingParametersPerFinger)}";
        }

        private static void PrepareTapingSelection(TapingFingersManager manager)
        {
            if (manager == null || manager.Pointer == IntPtr.Zero || manager.HasSelectedHand)
                return;

            try
            {
                var method = AccessTools.Method(typeof(TapingFingersManager), nameof(TapingFingersManager.SelectHand));
                var handType = method?.GetParameters()[0].ParameterType;
                var hand = GetFirstRealHandValue(handType);
                if (method == null || hand == null)
                {
                    ModLog.Warning("[BivouacDebug] SelectHand reflection repair unavailable");
                    return;
                }

                method.Invoke(manager, new[] { hand, 0 });
                SafeLog("TapingPrepareSelection", () => $"handValue={hand} {DescribeTaping(manager)}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[BivouacDebug] SelectHand repair failed: {ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}");
            }
        }

        private static bool EnsureCurrentModel(TapingFingersManager manager)
        {
            if (manager == null || manager.Pointer == IntPtr.Zero)
                return false;

            if (GetCurrentModelSafe(manager) != null)
                return true;

            try
            {
                var setter = AccessTools.Property(typeof(TapingFingersManager), "CurrentModel")?.GetSetMethod(true);
                if (setter == null)
                    return false;

                var models = manager.models;
                if (models == null)
                    return false;

                var modelsType = models.GetType();
                var count = ResolveCount(modelsType, models);
                if (count <= 0)
                    return false;

                var model = ResolveIndex(modelsType, models, 0);
                if (model == null)
                    return false;

                setter.Invoke(manager, new[] { model });
                return GetCurrentModelSafe(manager) != null;
            }
            catch (Exception ex)
            {
                SafeLog("TapingCurrentModelFailed", () => $"{ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}");
                return false;
            }
        }

        private static int ResolveCount(Type modelsType, object models)
        {
            var countProp = modelsType.GetProperty("Count");
            if (countProp != null)
                return Convert.ToInt32(countProp.GetValue(models, null));

            var lengthProp = modelsType.GetProperty("Length");
            if (lengthProp != null)
                return Convert.ToInt32(lengthProp.GetValue(models, null));

            return 0;
        }

        private static object ResolveIndex(Type modelsType, object models, int index)
        {
            var indexed = modelsType.GetProperty("Item", new[] { typeof(int) })?.GetGetMethod();
            if (indexed != null)
                return indexed.Invoke(models, new object[] { index });

            var get = modelsType.GetMethod("Get", new[] { typeof(int) });
            if (get != null)
                return get.Invoke(models, new object[] { index });

            return null;
        }

        private static object GetFirstRealHandValue(Type handType)
        {
            if (handType == null || !handType.IsEnum)
                return null;

            foreach (var value in Enum.GetValues(handType))
            {
                var name = value.ToString();
                if (!string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
                    return value;
            }

            return null;
        }

        private static void RecoverFromFailedActivation(TapingFingersManager manager, Il2CppSystem.Action onExitCallback,
            string reason)
        {
            CleanupTapingPartialState(manager, reason);
            InvokeExitCallback(onExitCallback, reason);
        }

        private static void CleanupTapingPartialState(TapingFingersManager manager, string reason)
        {
            if (manager == null || manager.Pointer == IntPtr.Zero)
                return;

            try
            {
                TryInvokePrivate(manager, "Deactivate");
                SetTransformActive(manager.visualsParent, false);
                SetTransformActive(manager.armsOffsetForTransition, false);
                SetCanvasGroupVisible(manager.inputPromptGroup, false);
                SetCanvasGroupVisible(manager.stickGroup, false);

                if (manager.group != null)
                    SetCanvasGroupVisible(manager.group, false);

                if (manager.bandageCanvasGroup != null)
                    SetCanvasGroupVisible(manager.bandageCanvasGroup, false);

                if (manager.tapeRenderer != null)
                    manager.tapeRenderer.enabled = false;

                SafeLog("TapingActivationCleanup", () => $"{reason} cleaned {DescribeTaping(manager)}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[BivouacDebug] Cleanup failed after {reason}: {ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}");
            }
        }

        private static void SetTransformActive(Transform transform, bool active)
        {
            if (transform != null)
                transform.gameObject.SetActive(active);
        }

        private static void SetCanvasGroupVisible(CanvasGroup group, bool visible)
        {
            if (group == null)
                return;

            group.gameObject.SetActive(visible);
            group.alpha = visible ? 1f : 0f;
            group.interactable = visible;
            group.blocksRaycasts = visible;
        }

        private static bool ShouldLogEvent(string eventName)
        {
            return KeepLogEvent.Contains(eventName);
        }

        private static void TryInvokePrivate(TapingFingersManager manager, string methodName)
        {
            try
            {
                var method = AccessTools.Method(typeof(TapingFingersManager), methodName);
                if (method == null)
                {
                    ModLog.Warning($"[BivouacDebug] {methodName} method not found");
                    return;
                }

                method.Invoke(manager, null);
                ModLog.Debug($"[BivouacDebug] {methodName} invoked for TapingFingersManager");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[BivouacDebug] {methodName} invoke failed: {ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}");
            }
        }

        private static void InvokeExitCallback(Il2CppSystem.Action callback, string reason)
        {
            if (callback == null)
                return;

            try
            {
                callback.Invoke();
                ModLog.Debug($"[BivouacDebug] Exit callback invoked after {reason}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[BivouacDebug] Exit callback failed after {reason}: {ex.GetType().Name}:{GameInterop.FirstLine(ex.Message)}");
            }
        }
    }
}
