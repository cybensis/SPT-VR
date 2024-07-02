using EFT.InputSystem;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static TarkovVR.Source.Controls.InputHandlers;

namespace TarkovVR.Source.Controls
{
    internal static class VRInputManager
    {
        public static Dictionary<ECommand, IInputHandler> inputHandlers;
        public static Dictionary<ECommand, IInputHandler> menuInputHandlers;
        static VRInputManager()
        {
            inputHandlers = new Dictionary<ECommand, IInputHandler>
            {
                { ECommand.Jump, new InputHandlers.JumpInputHandler() },
                { ECommand.ToggleSprinting, new InputHandlers.SprintInputHandler() },
                { ECommand.ReloadWeapon, new InputHandlers.ReloadInputHandler() },
                { ECommand.ToggleShooting, new InputHandlers.FireHandler() },
                { ECommand.ScrollNext, new InputHandlers.ScrollHandler() },
                { ECommand.ToggleBreathing, new InputHandlers.BreathingHandler() },
                { ECommand.ToggleAlternativeShooting, new InputHandlers.AimHandler() },
                { ECommand.SelectFirstPrimaryWeapon, new InputHandlers.SelectWeaponHandler() },
                { ECommand.ChangeScopeMagnification, new InputHandlers.ScopeZoomHandler() },
                { ECommand.CheckAmmo, new InputHandlers.CheckAmmoHandler() },
                { ECommand.ChangeWeaponMode, new InputHandlers.FireModeHandler() },
                { ECommand.CheckChamber, new InputHandlers.CheckChamberHandler() },
                { ECommand.SelectFastSlot4, new InputHandlers.QuickSlotHandler() },
                { ECommand.BeginInteracting, new InputHandlers.InteractHandler() },
                { ECommand.ToggleInventory, new InputHandlers.InventoryHandler() },
                { ECommand.TryHighThrow, new InputHandlers.GrenadeHandler() },
                { ECommand.Escape, new InputHandlers.EscapeHandler() },
                { ECommand.ExamineWeapon, new InputHandlers.ExamineWeaponHandler() },
                // Add other command handlers here
            };
            menuInputHandlers = new Dictionary<ECommand, IInputHandler>
            {
                { ECommand.Escape, new InputHandlers.EscapeHandler() },
                // Add other command handlers here
            };
        }

        public static void UpdateCommands(ref List<ECommand> commands)
        {
            if (VRGlobals.inGame && !VRGlobals.menuOpen)
            {
                foreach (var kvp in inputHandlers)
                {
                    ECommand command = 0;
                    IInputHandler handler = kvp.Value;
                    handler.UpdateCommand(ref command);
                    if (command != 0) // Ensure to check for a valid command
                    {
                        commands.Add(command);
                    }
                }
            }
            else {
                foreach (var kvp in menuInputHandlers)
                {
                    ECommand command = 0;
                    IInputHandler handler = kvp.Value;
                    handler.UpdateCommand(ref command);
                    if (command != 0) // Ensure to check for a valid command
                    {
                        commands.Add(command);
                    }
                }
            }
        }

    }
}
//0: LeanLockRight
//1: LeanLockLeft
//2: Shoot
//3: Aim
//4: ChangeAimScope
//5: ChangeAimScopeMagnification
//6: Nidnod
//7: ToggleGoggles
//8: ToggleHeadLight
//9: SwitchHeadLight
//10: ToggleVoip
//11: PushToTalk
//12: Mumble
//13: MumbleDropdown
//14: MumbleQuick
//15: WatchTime
//16: WatchTimerAndExits
//17: Tactical
//18: NextTacticalDevice
//19: Next
//20: Previous
//21: Interact
//22: ThrowGrenade
//23: ReloadWeapon
//24: QuickReloadWeapon
//25: DropBackpack
//26: NextMagazine
//27: PreviousMagazine
//28: ChangePointOfView
//29: CheckAmmo
//30: ShootingMode
//31: ForceAutoWeaponMode
//32: CheckFireMode
//33: CheckChamber
//34: ChamberUnload
//35: UnloadMagazine
//36: Prone
//37: Sprint
//38: Duck
//39: NextWalkPose
//40: PreviousWalkPose
//41: Walk
//42: BlindShootAbove
//43: BlindShootRight
//44: StepRight
//45: StepLeft
//46: ExamineWeapon
//47: FoldStock
//48: Inventory
//49: Jump
//50: Knife
//51: QuickKnife
//52: PrimaryWeaponFirst
//53: PrimaryWeaponSecond
//54: SecondaryWeapon
//55: QuickSecondaryWeapon
//56: Slot4
//57: Slot5
//58: Slot6
//59: Slot7
//60: Slot8
//61: Slot9
//62: Slot0
//63: OpticCalibrationSwitchUp 
//64: OpticCalibrationSwitchDown
//65: MakeScreenshot
//66: ThrowItem
//67: Breath
//68: ToggleInfo
//69: Console
//70: PressSlot4
//71: PressSlot5
//72: PressSlot6
//73: PressSlot7
//74: PressSlot8
//75: PressSlot9
//76: PressSlot0
//77: F1
//78: DoubleF1
//79: F2
//80: DoubleF2
//81: F3
//82: DoubleF3
//83: F4
//84: DoubleF4
//85: F5
//86: DoubleF5
//87: F6
//88: DoubleF6
//89: F7
//90: DoubleF7
//91: F8
//92: DoubleF8
//93: F9
//94: DoubleF9
//95: F10
//96: DoubleF10
//97: F11
//98: DoubleF11
//99: F12
//100: DoubleF12
//101: Enter
//102: Escape
//103: HighThrow
//104: LowThrow