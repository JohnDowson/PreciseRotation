using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace PreciseRotation
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    internal class PreciseRotation : BaseUnityPlugin
    {
        public const string PluginGUID = "com.github.johndowson.PreciseRotation";
        public const string PluginName = "PreciseRotation";
        public const string PluginVersion = "26.2.0";
        private static readonly Harmony harmony = new(PluginGUID);

        private ConfigEntry<int> RotationStepsCoarse;
        private ConfigEntry<int> RotationStepsFineMultiplier;

        private ConfigEntry<bool> ToggleRotation;
        private ConfigEntry<KeyCode> RotationModifierKeyboard;
        private ConfigEntry<InputManager.GamepadButton> RotationModifierGamepad;
        private ButtonConfig RotationModifier;

        private ConfigEntry<KeyCode> NextAxisKeyboard;
        private ConfigEntry<InputManager.GamepadButton> NextAxisGamepad;
        private ButtonConfig NextAxis;

        public static CustomLocalization Localization = LocalizationManager.Instance.GetLocalization();

        private static Rotator rotator;

        private static Sprite X, Y, Z;

        private void Awake()
        {
            X = AssetUtils.LoadSpriteFromFile("PreciseRotation/Assets/axis_x.png");
            Y = AssetUtils.LoadSpriteFromFile("PreciseRotation/Assets/axis_y.png");
            Z = AssetUtils.LoadSpriteFromFile("PreciseRotation/Assets/axis_z.png");

            ToggleRotation = Config.Bind("Controls", "ToggleRotation", false, "Key to toggle precise rotation");
            RotationModifierKeyboard = Config.Bind("Controls", "RotationModifierKeyboard", KeyCode.LeftAlt, "Key to toggle precise rotation");
            RotationModifierGamepad = Config.Bind("Controls", "RotationModifierGamepad", InputManager.GamepadButton.None, "Key to toggle precise rotation");
            RotationModifier = new ButtonConfig
            {
                Name = "RotationModifier",
                Config = RotationModifierKeyboard,
                GamepadConfig = RotationModifierGamepad,
                HintToken = "$RotationModifierHint",
                BlockOtherInputs = false,
            };
            InputManager.Instance.AddButton(PluginGUID, RotationModifier);

            NextAxisKeyboard = Config.Bind("Controls", "NextAxisKeyboard", KeyCode.Period, "Key to toggle rotation axis");
            NextAxisGamepad = Config.Bind("Controls", "NextAxisGamepad", InputManager.GamepadButton.None, "Key to toggle rotation axis");
            NextAxis = new ButtonConfig
            {
                Name = "NextAxis",
                Config = NextAxisKeyboard,
                GamepadConfig = NextAxisGamepad,
                HintToken = "$NextAxisHint",
                BlockOtherInputs = true,
            };
            InputManager.Instance.AddButton(PluginGUID, NextAxis);

            RotationStepsCoarse = Config.Bind("Rotation", "StepsCoarse", 16, "Base number of rotation steps per 180 degrees, Valheim's default is 16");
            RotationStepsFineMultiplier = Config.Bind("Rotation", "FineMultiplier", 2, "Multiply StepsCoarse by this number to get precise mode steps per 180");
            rotator = new Rotator(RotationStepsCoarse.Value, RotationStepsFineMultiplier.Value);

            harmony.PatchAll();

            Jotunn.Logger.LogInfo("PreciseRotation has landed");
        }

        private void Update()
        {
            if (ZInput.instance != null && Player.m_localPlayer != null)
            {
                if (ToggleRotation.Value && ZInput.GetButtonDown(RotationModifier.Name))
                {
                    rotator.Precision = !rotator.Precision;
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Precise Rotation " + (rotator.Precision ? "On" : "Off"));
                }
                else
                {
                    rotator.Precision = ZInput.GetButton(RotationModifier.Name);
                }

                Jotunn.Logger.LogFatal($"Prec: {rotator.Precision};");

                if (ZInput.GetButtonDown(NextAxis.Name))
                {
                    rotator.NextAxis();
                    Sprite icon = rotator.CurrentAxis switch
                    {
                        Rotator.Axis.X => X,
                        Rotator.Axis.Y => Y,
                        Rotator.Axis.Z => Z,
                        _ => throw new System.ArgumentOutOfRangeException(),
                    };
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Rotation Axis", icon: icon);
                }
            }
        }

        public static Quaternion GetRotatation(Player player)
        {
            if (player != Player.m_localPlayer)
            {
                return Quaternion.Euler(0f, player.m_placeRotationDegrees * (float)player.m_placeRotation, 0f);
            }

            return rotator.GetRotatation();
        }

        private struct Rotator
        {
            public Rotator(int stepsCoarse, int fineMult)
            {
                x = y = z = 0.0f;
                Precision = false;
                DegreesPerStep = 180.0f / (float)(stepsCoarse * fineMult);
                this.fineMult = fineMult;
            }
            public enum Axis
            {
                X,
                Y,
                Z,
            }
            public Axis CurrentAxis = Axis.Y;

            public float DegreesPerStep;
            public int fineMult;
            public float x, y, z;
            public bool Precision;

            public void Rotate(int steps)
            {
                steps = Precision ? steps : steps * fineMult;
                float degrees = DegreesPerStep * (float)steps;
                switch (CurrentAxis)
                {
                    case Axis.X:
                        {
                            x += degrees;
                            return;
                        }
                    case Axis.Y:
                        {
                            y += degrees;
                            break;
                        }
                    case Axis.Z:
                        {
                            z += degrees;
                            break;
                        }
                }


            }

            public readonly Quaternion GetRotatation() => Quaternion.Euler(x, y, z);


            public void NextAxis()
            {
                CurrentAxis = CurrentAxis switch
                {
                    Axis.X => Axis.Y,
                    Axis.Y => Axis.Z,
                    Axis.Z => Axis.X,
                    _ => throw new System.ArgumentOutOfRangeException(),
                };
            }
        }


        [HarmonyPatch(typeof(Player))]
        public static class PieceRotation_Patch
        {

            [HarmonyPatch("UpdatePlacementGhost")]
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> UpdatePlacementGhostPatch(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                for (var i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Ldc_R4 &&
                        codes[i + 1].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 2].opcode == OpCodes.Ldfld &&
                        codes[i + 3].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 4].opcode == OpCodes.Ldfld &&
                        codes[i + 5].opcode == OpCodes.Conv_R4 &&
                        codes[i + 6].opcode == OpCodes.Mul &&
                        codes[i + 7].opcode == OpCodes.Ldc_R4 &&
                        codes[i + 8].opcode == OpCodes.Call)
                    {
                        codes[i].opcode = OpCodes.Ldarg_0;
                        codes[i + 1] = new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo((Player player) => PreciseRotation.GetRotatation(player)));
                        for (int y = 2; y <= 8; y++)
                        {
                            codes[i + y].opcode = OpCodes.Nop;
                        }
                    }
                }
                return codes.AsEnumerable();
            }

            [HarmonyPatch("UpdatePlacement")]
            [HarmonyPrefix]
            static void RememberOldRotation(Player __instance, bool takeInput, float dt, ref int __state)
            {
                __state = __instance.m_placeRotation;
            }
            [HarmonyPatch("UpdatePlacement")]
            [HarmonyPostfix]
            static void StoreRotationDifference(Player __instance, bool takeInput, float dt, ref int __state)
            {
                if (Player.m_localPlayer != __instance)
                {
                    return;
                }

                int difference = __instance.m_placeRotation - __state;
                rotator.Rotate(difference);
            }
        }
    }
}

