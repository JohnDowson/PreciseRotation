using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace PreciseRotation {
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    internal class PreciseRotation : BaseUnityPlugin {
        public const string PluginGUID = "com.github.johndowson.PreciseRotation";
        public const string PluginName = "PreciseRotation";
        public const string PluginVersion = "26.2.2";
        private static readonly Harmony harmony = new(PluginGUID);


        private ConfigEntry<bool> ToggleRotation;
        private ButtonConfig RotationModifier;
        private ButtonConfig RotationReset;
        private ButtonConfig NextAxis;
        private ButtonConfig PreviousAxis;

        public static CustomLocalization Localization = LocalizationManager.Instance.GetLocalization();

        private static Rotator rotator;

        private static Sprite X, Y, Z;

        private ButtonConfig makeBinding(
            string name, string description,
            KeyCode keyboardDefault, InputManager.GamepadButton gamepadDefault,
            bool blockOtherInputs = false
        ) {
            return new ButtonConfig {
                Name = name,
                Config = Config.Bind("Controls", $"{name}Keyboard", keyboardDefault, description),
                GamepadConfig = Config.Bind("Controls", $"{name}Gamepad", gamepadDefault, description),
                HintToken = $"${name}Hint",
                BlockOtherInputs = blockOtherInputs,
            };
        }

        private void Awake() {
            X = AssetUtils.LoadSpriteFromFile("PreciseRotation/Assets/axis_x.png");
            Y = AssetUtils.LoadSpriteFromFile("PreciseRotation/Assets/axis_y.png");
            Z = AssetUtils.LoadSpriteFromFile("PreciseRotation/Assets/axis_z.png");
            X ??= AssetUtils.LoadSpriteFromFile("disregardthatisuck-PreciseRotation/PreciseRotation/Assets/axis_x.png");
            Y ??= AssetUtils.LoadSpriteFromFile("disregardthatisuck-PreciseRotation/PreciseRotation/Assets/axis_y.png");
            Z ??= AssetUtils.LoadSpriteFromFile("disregardthatisuck-PreciseRotation/PreciseRotation/Assets/axis_z.png");

            if (new[] { X, Y, Z }.Any((s) => s is null)) {
                Logger.LogWarning("Could not load axis icons. Are you using a weird mod manager?");
            }

            ToggleRotation = Config.Bind("Controls", "ToggleRotation", false, "When `true` pressing RotationModifier button toggless precise mode on and off");

            RotationModifier = makeBinding(nameof(RotationModifier), "Key to toggle precise rotation", KeyCode.LeftAlt, InputManager.GamepadButton.None);
            InputManager.Instance.AddButton(PluginGUID, RotationModifier);

            RotationReset = makeBinding(nameof(RotationReset), "Key reset rotation in current axis", KeyCode.Slash, InputManager.GamepadButton.None);
            InputManager.Instance.AddButton(PluginGUID, RotationReset);

            NextAxis = makeBinding(nameof(NextAxis), "Key to switch to next rotation axis", KeyCode.Period, InputManager.GamepadButton.None, true);
            InputManager.Instance.AddButton(PluginGUID, NextAxis);

            PreviousAxis = makeBinding(nameof(PreviousAxis), "Key to switch to next rotation axis", KeyCode.Comma, InputManager.GamepadButton.None, true);
            InputManager.Instance.AddButton(PluginGUID, PreviousAxis);

            var RotationStepsCoarse = Config.Bind("Rotation", "StepsCoarse", 16, "Base number of rotation steps per 180 degrees, Valheim's default is 16");
            var RotationStepsFineMultiplier = Config.Bind("Rotation", "FineMultiplier", 2, "Multiply StepsCoarse by this number to get precise mode steps per 180");
            rotator = new Rotator(RotationStepsCoarse, RotationStepsFineMultiplier);

            harmony.PatchAll();

            Jotunn.Logger.LogInfo("PreciseRotation has landed");
        }

        private void Update() {
            if (ZInput.instance != null && Player.m_localPlayer != null) {
                if (ToggleRotation.Value && ZInput.GetButtonDown(RotationModifier.Name)) {
                    rotator.Precision = !rotator.Precision;
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Precise Rotation " + (rotator.Precision ? "On" : "Off"));
                } else {
                    rotator.Precision = ZInput.GetButton(RotationModifier.Name);
                }

                if (ZInput.GetButtonDown(RotationReset.Name)) {
                    rotator.ResetCurrent();
                }

                var updatedAxis = false;
                if (ZInput.GetButtonDown(NextAxis.Name)) {
                    rotator.NextAxis();
                    updatedAxis = true;
                }
                if (ZInput.GetButtonDown(PreviousAxis.Name)) {
                    rotator.PrevAxis();
                    updatedAxis = true;
                }
                if (updatedAxis) {
                    Sprite icon = rotator.CurrentAxis switch {
                        Rotator.Axis.X => X,
                        Rotator.Axis.Y => Y,
                        Rotator.Axis.Z => Z,
                        _ => throw new System.ArgumentOutOfRangeException(),
                    };
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Rotation Axis", icon: icon);
                }
            }
        }

        public static Quaternion GetRotatation(Player player) {
            if (player != Player.m_localPlayer) {
                return Quaternion.Euler(0f, player.m_placeRotationDegrees * (float)player.m_placeRotation, 0f);
            }

            return rotator.GetRotatation();
        }

        private struct Rotator {
            public Rotator(ConfigEntry<int> stepsCoarse, ConfigEntry<int> fineMult) {
                x = y = z = 0.0f;
                Precision = false;
                DegreesPerStep = 180.0f / (float)(stepsCoarse.Value * fineMult.Value);
                this.fineMult = fineMult.Value;
                this.stepsCoarse = stepsCoarse.Value;
                stepsCoarse.SettingChanged += recalculate;
            }

            private void recalculate(object _sender, System.EventArgs eventArgs) {
                var setting = ((SettingChangedEventArgs)eventArgs).ChangedSetting;
                var key = setting.Definition.Key;
                if (key == "StepsCoarse") {
                    stepsCoarse = (int)setting.BoxedValue;
                } else if (key == "StepsCoarse") {
                    fineMult = (int)setting.BoxedValue;
                } else { return; }

                x = y = z = 0.0f;
                DegreesPerStep = 180.0f / (float)(stepsCoarse * fineMult);
            }

            public enum Axis {
                X, Y, Z,
            }
            public Axis CurrentAxis = Axis.Y;

            public float DegreesPerStep;
            public int fineMult, stepsCoarse;
            public float x, y, z;
            public bool Precision;

            public void ResetCurrent() {
                switch (CurrentAxis) {
                    case Axis.X: {
                            x = 0f;
                            return;
                        }
                    case Axis.Y: {
                            y = 0f;
                            return;
                        }
                    case Axis.Z: {
                            z = 0f;
                            return;
                        }
                }
            }

            public void Rotate(int steps) {
                steps = Precision ? steps : steps * fineMult;
                float degrees = DegreesPerStep * (float)steps;
                switch (CurrentAxis) {
                    case Axis.X: {
                            x += degrees;
                            return;
                        }
                    case Axis.Y: {
                            y += degrees;
                            return;
                        }
                    case Axis.Z: {
                            z += degrees;
                            return;
                        }
                }
            }

            public readonly Quaternion GetRotatation() => Quaternion.Euler(x, y, z);

            public void NextAxis() {
                CurrentAxis = CurrentAxis switch {
                    Axis.X => Axis.Y,
                    Axis.Y => Axis.Z,
                    Axis.Z => Axis.X,
                    _ => throw new System.ArgumentOutOfRangeException(),
                };
            }

            public void PrevAxis() {
                CurrentAxis = CurrentAxis switch {
                    Axis.X => Axis.Z,
                    Axis.Y => Axis.X,
                    Axis.Z => Axis.Y,
                    _ => throw new System.ArgumentOutOfRangeException(),
                };
            }
        }


        [HarmonyPatch(typeof(Player))]
        public static class PieceRotation_Patch {

            [HarmonyPatch("UpdatePlacementGhost")]
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> UpdatePlacementGhostPatch(IEnumerable<CodeInstruction> instructions) {
                var codes = new List<CodeInstruction>(instructions);
                for (var i = 0; i < codes.Count; i++) {
                    if (codes[i].opcode == OpCodes.Ldc_R4 &&
                        codes[i + 1].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 2].opcode == OpCodes.Ldfld &&
                        codes[i + 3].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 4].opcode == OpCodes.Ldfld &&
                        codes[i + 5].opcode == OpCodes.Conv_R4 &&
                        codes[i + 6].opcode == OpCodes.Mul &&
                        codes[i + 7].opcode == OpCodes.Ldc_R4 &&
                        codes[i + 8].opcode == OpCodes.Call) {
                        codes[i].opcode = OpCodes.Ldarg_0;
                        codes[i + 1] = new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo((Player player) => PreciseRotation.GetRotatation(player)));
                        for (int y = 2; y <= 8; y++) {
                            codes[i + y].opcode = OpCodes.Nop;
                        }
                    }
                }
                return codes.AsEnumerable();
            }

            [HarmonyPatch("UpdatePlacement")]
            [HarmonyPrefix]
            static void RememberOldRotation(Player __instance, bool takeInput, float dt, ref int __state) {
                __state = __instance.m_placeRotation;
            }
            [HarmonyPatch("UpdatePlacement")]
            [HarmonyPostfix]
            static void StoreRotationDifference(Player __instance, bool takeInput, float dt, ref int __state) {
                if (Player.m_localPlayer != __instance) {
                    return;
                }

                int difference = __instance.m_placeRotation - __state;
                rotator.Rotate(difference);
            }
        }
    }
}

