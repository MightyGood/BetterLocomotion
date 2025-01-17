using System;
using System.Linq;
using System.Reflection;
using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;
using MelonLoader;
using VRC.Animation;
using BuildInfo = BetterLocomotion.BuildInfo;
using Main = BetterLocomotion.Main;

/*
 * A lot of code was taken from the BetterDirections mod
 * Special thanks to Davi
 * https://github.com/d-magit/VRC-Mods 
 */

[assembly: AssemblyCopyright("Created by " + BuildInfo.Author)]
[assembly: MelonInfo(typeof(Main), BuildInfo.Name, BuildInfo.Version, BuildInfo.Author)]
[assembly: MelonGame("VRChat", "VRChat")]
[assembly: MelonColor(ConsoleColor.Magenta)]
[assembly: MelonOptionalDependencies("UIExpansionKit")]

namespace BetterLocomotion
{
    public static class BuildInfo
    {
        public const string Name = "BetterLocomotion";
        public const string Author = "Erimel, Davi & AxisAngle";
        public const string Version = "1.0.0";
    }

    internal static class UIXManager { public static void OnApplicationStart() => UIExpansionKit.API.ExpansionKitApi.OnUiManagerInit += Main.VRChat_OnUiManagerInit; }

    public class Main : MelonMod
    {
        private enum Locomotion { Head, Hip, Chest }
        internal static MelonLogger.Instance Logger;
        private static HarmonyLib.Harmony _hInstance;

        // Wait for Ui Init so XRDevice.isPresent is defined
        public override void OnApplicationStart()
        {
            Logger = LoggerInstance;
            _hInstance = HarmonyInstance;

            WaitForUiInit();
            InitializeSettings();
            OnPreferencesSaved();

            // Patches
            MethodsResolver.ResolveMethods();
            if (MethodsResolver.RestoreTrackingAfterCalibration != null)
                HarmonyInstance.Patch(MethodsResolver.RestoreTrackingAfterCalibration, null,
                    new HarmonyMethod(typeof(Main), nameof(VRCTrackingManager_RestoreTrackingAfterCalibration)));
            if (MethodsResolver.IKTweaksApplyStoredCalibration != null)
                HarmonyInstance.Patch(MethodsResolver.IKTweaksApplyStoredCalibration,
                    new HarmonyMethod(typeof(Main), nameof(VRCTrackingManager_RestoreTrackingAfterCalibration)));

            Logger.Msg("Successfully loaded!");
        }

        private static MelonPreferences_Entry<Locomotion> _locomotionMode;
        private static MelonPreferences_Entry<bool> _forceUseBones;
        private static MelonPreferences_Entry<bool> _hasJoystickDrift;
        private static MelonPreferences_Entry<float> _joystickDriftAmount;
        private static void InitializeSettings()
        {
            MelonPreferences.CreateCategory("BetterLocomotion", "BetterLocomotion");

            _locomotionMode = MelonPreferences.CreateEntry("BetterLocomotion", "LocomotionMode", Locomotion.Head, "Locomotion mode");
            _hasJoystickDrift = MelonPreferences.CreateEntry("BetterLocomotion", "HasJoystickDrift", false, "Enable joystick drift compensation");
            _joystickDriftAmount = MelonPreferences.CreateEntry("BetterLocomotion", "JoystickDriftAmount", 0.3f, "Joystick drift amount (0-1)");

            _forceUseBones = MelonPreferences.CreateEntry("BetterLocomotion", "ForceUseBones", false, "Use bones instead of trackers (not recommended)");
        }

        private static void WaitForUiInit()
        {
            if (MelonHandler.Mods.Any(x => x.Info.Name.Equals("UI Expansion Kit")))
                typeof(UIXManager).GetMethod("OnApplicationStart")!.Invoke(null, null);
            else
            {
                Logger.Warning("UIExpansionKit (UIX) was not detected. Using coroutine to wait for UiInit. Please consider installing UIX.");
                static IEnumerator OnUiManagerInit()
                {
                    while (VRCUiManager.prop_VRCUiManager_0 == null)
                        yield return null;
                    VRChat_OnUiManagerInit();
                }
                MelonCoroutines.Start(OnUiManagerInit());
            }
        }

        // Apply the patch
        public static void VRChat_OnUiManagerInit()
        {
            if (XRDevice.isPresent)
            {
                Logger.Msg("XRDevice detected. Initializing...");
                try
                {
                    foreach (var info in typeof(VRCMotionState).GetMethods().Where(method =>
                        method.Name.Contains("Method_Public_Void_Vector3_Single_") && !method.Name.Contains("PDM")))
                        _hInstance.Patch(info, new HarmonyMethod(typeof(Main).GetMethod(nameof(Prefix))));
                    Logger.Msg("Successfully loaded!");
                }
                catch (Exception e)
                {
                    Logger.Warning("Failed to initialize mod!");
                    Logger.Error(e);
                }
            }
            else Logger.Warning("Mod is VR-Only.");
        }

        private static VRCPlayer GetLocalPlayer() => VRCPlayer.field_Internal_Static_VRCPlayer_0;

        private static SteamVR_ControllerManager GetSteamVRControllerManager()
        {
            var inputProcessor = VRCInputManager.field_Private_Static_Dictionary_2_InputMethod_VRCInputProcessor_0;
            if (!(inputProcessor?.Count > 0)) return null;
            var lInput = inputProcessor[VRCInputManager.InputMethod.Vive];
            if (lInput == null) return null;
            var lViveInput = lInput.TryCast<VRCInputProcessorVive>();

            SteamVR_ControllerManager lResult = null;
            if (lViveInput != null) lResult = lViveInput.field_Private_SteamVR_ControllerManager_0;
            return lResult;
        }

        private static bool CheckIfInFbt() => GetLocalPlayer().field_Private_VRC_AnimationController_0.
            field_Private_IkController_0.field_Private_IkType_0 is IkController.IkType.SixPoint or IkController.IkType.FourPoint;

        private static void VRCTrackingManager_RestoreTrackingAfterCalibration() //Gets the trackers or bones and creates the offset GameObjects
        {
            _isInFbt = true;

            var getTrackerHips = GetTracker(HumanBodyBones.Hips);
            _hipTransform = getTrackerHips == null || _forceUseBones.Value
                            ? GetLocalPlayer().field_Internal_Animator_0.GetBoneTransform(HumanBodyBones.Hips)
                            : getTrackerHips;

            var getTrackerChest = GetTracker(HumanBodyBones.Chest);
            _chestTransform = getTrackerChest == null || getTrackerChest == _hipTransform || _forceUseBones.Value
                ? GetLocalPlayer().field_Internal_Animator_0.GetBoneTransform(HumanBodyBones.Chest)
                : getTrackerChest;

            var rotation = Quaternion.FromToRotation(_headTransform.up, Vector3.up) * _headTransform.rotation;
            _offsetHip = new GameObject
            {
                transform =
                {
                    parent = _hipTransform,
                    rotation = rotation
                }
            };
            _offsetChest = new GameObject
            {
                transform =
                {
                    parent = _chestTransform,
                    rotation = rotation
                }
            };
        }

        private static readonly HumanBodyBones[] LinkedBones = { HumanBodyBones.Hips, HumanBodyBones.Chest };

        private static Transform GetTracker(HumanBodyBones bodyPart) //Gets the SteamVR tracker for a certain bone
        {
            var puckArray = GetSteamVRControllerManager().field_Public_ArrayOf_GameObject_0;
            for (var i = 0; i < puckArray.Length - 2; i++)
            {
                if (FindAssignedBone(puckArray[i + 2].transform) == bodyPart)
                    return puckArray[i + 2].transform;
            }
            return HeadTransform;
        }

        private static HumanBodyBones FindAssignedBone(Transform trackerTransform) //Finds the nearest bone to the transform of a SteamVR tracker
        {
            var result = HumanBodyBones.LastBone;
            var distance = float.MaxValue;
            foreach (var bone in LinkedBones)
            {
                var lBoneTransform = GetLocalPlayer().field_Internal_Animator_0.GetBoneTransform(bone);
                if (lBoneTransform == null) continue;
                var lDistanceToPuck = Vector3.Distance(lBoneTransform.position, trackerTransform.position);
                if (!(lDistanceToPuck < distance)) continue;
                distance = lDistanceToPuck;
                result = bone;
            }
            return result;
        }

        private static bool _isInFbt;
        private static int _isInFbtTimer;
        private static GameObject _offsetHip, _offsetChest;
        private static Transform _headTransform, _hipTransform, _chestTransform;
        private static Transform HeadTransform => //Gets the head transform
            _headTransform ??= Resources.FindObjectsOfTypeAll<NeckMouseRotator>()[0].transform
                .Find(Environment.CurrentDirectory.Contains("vrchat-vrchat") ? "CenterEyeAnchor" : "Camera (eye)");

        // Substitute the direction from the original method with our own
        public static void Prefix(ref Vector3 __0) { __0 = CalculateDirection(__0); }

        // Fixes the game's original direction to match the preferred one
        private static Vector3 CalculateDirection(Vector3 rawVelo)
        {
            if (_hasJoystickDrift.Value && Mathf.Clamp(_joystickDriftAmount.Value, 0, 0.98f) > (Math.Abs(Input.GetAxisRaw("Vertical")) + Math.Abs(Input.GetAxisRaw("Horizontal"))))
                return Vector3.zero;

            var @return = _locomotionMode.Value switch
            {
                Locomotion.Hip when _isInFbt && _hipTransform != null => CalculateLocomotion(_offsetHip.transform, rawVelo),
                Locomotion.Chest when _isInFbt && _chestTransform != null => CalculateLocomotion(_offsetChest.transform, rawVelo),
                _ => CalculateLocomotion(HeadTransform, rawVelo),
            };

            _isInFbtTimer++;
            if (_isInFbtTimer <= 100) return @return;
            _isInFbtTimer = 0;
            _isInFbt = CheckIfInFbt();

            return @return;
        }

        private static Vector3 CalculateLocomotion(Component trackerTransform, Vector3 headVelo) //Thanks AxisAngle for the logic for the angles
        {
            //Undo VRChat's locomotion to get the movement independant of where the player is looking at.
            Vector3 inputDirection = Quaternion.Inverse(Quaternion.LookRotation(Vector3.Cross(Vector3.Cross(Vector3.up, HeadTransform.forward), Vector3.up))) * headVelo;
            //Calculate locomotion with better directions according to the reference (tracker or head) we want.
            return Quaternion.FromToRotation(trackerTransform.transform.up, Vector3.up) * trackerTransform.transform.rotation * inputDirection;
        }
    }
}