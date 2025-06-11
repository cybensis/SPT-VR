using HarmonyLib;
using UnityEngine;
using EFT;
using EFT.UI;
using EFT.InputSystem;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using JetBrains.Annotations;
using static EFT.UI.ItemsPanel;
using System.Threading.Tasks;
using TarkovVR.Patches.Core.Equippables;
using TarkovVR.Patches.Visuals;
using TarkovVR.Source.Player.VRManager;
using EFT.UI.DragAndDrop;
using Valve.VR;
using TarkovVR.Source.Settings;
using System;
using TarkovVR.Source.UI;
using UnityEngine.EventSystems;
using TarkovVR.Patches.Misc;
using static TarkovVR.Patches.UI.UIPatchShared;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class InventoryStashPatches
    {




        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemView), "OnClick")]
        private static bool HandleItemClick(ItemView __instance, PointerEventData.InputButton button, Vector2 position, bool doubleClick)
        {
            //Plugin.MyLog.LogError($"[TarkovVR] Click: button={button}, doubleClick={doubleClick}");
            if (__instance.ItemUiContext == null || !__instance.IsSearched)
            {
                return false;
            }
            // Use left and right grip to simulate ccontrol and alt click for items
            bool flag = SteamVR_Actions._default.RightGrip.state;
            bool flag2 = SteamVR_Actions._default.LeftGrip.state;
            bool flag3 = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            global::ItemInfoInteractionsAbstractClass<EItemInfoButton> newContextInteractions = __instance.NewContextInteractions;
            switch (button)
            {
                case PointerEventData.InputButton.Left:
                    {
                        if (!(flag || flag3) && doubleClick)
                        {

                            bool flag4 = __instance.ItemController is EFT.Player.PlayerInventoryController;
                            bool flag5 = Comfort.Common.Singleton<SharedGameSettingsClass>.Instance.Game.Settings.ItemQuickUseMode.Value switch
                            {
                                GClass1053.EItemQuickUseMode.Disabled => false,
                                GClass1053.EItemQuickUseMode.InRaidOnly => flag4,
                                GClass1053.EItemQuickUseMode.InRaidAndInLobby => true,
                                _ => throw new ArgumentOutOfRangeException(),
                            };
                            if ((__instance.Item is FoodDrinkItemClass || __instance.Item is MedsItemClass) && flag5)
                            {
                                if (!newContextInteractions.ExecuteInteraction(EItemInfoButton.Use))
                                {
                                    newContextInteractions.ExecuteInteraction(EItemInfoButton.UseAll);
                                }
                                break;
                            }
                            if (newContextInteractions.ExecuteInteraction(__instance.Item.IsContainer ? EItemInfoButton.Open : EItemInfoButton.Inspect))
                            {
                                break;
                            }
                        }
                        SimpleTooltip tooltip = __instance.ItemUiContext.Tooltip;
                        if (flag || flag3)
                        {
                            GStruct454 gStruct = flag ? __instance.ItemUiContext.QuickFindAppropriatePlace(__instance.ItemContext, __instance.ItemController) : __instance.ItemUiContext.QuickMoveToSortingTable(__instance.Item);
                            if (gStruct.Failed || !__instance.ItemController.CanExecute(gStruct.Value))
                            {
                                break;
                            }
                            if (gStruct.Value is GInterface401 { ItemsDestroyRequired: not false } destroyResult)
                            {
                                NotificationManagerClass.DisplayWarningNotification(new GClass3823(__instance.Item, destroyResult.ItemsToDestroy).GetLocalizedDescription());
                                break;
                            }
                            string itemSound = __instance.Item.ItemSound;
                            __instance.ItemController.RunNetworkTransaction(gStruct.Value);
                            if (tooltip != null)
                                tooltip.Close();
                            {
                            }
                            Comfort.Common.Singleton<GUISounds>.Instance.PlayItemSound(itemSound, EInventorySoundType.pickup);
                        }
                        else if (flag2)
                        {
                            newContextInteractions.ExecuteInteraction(EItemInfoButton.Equip);
                            if (tooltip != null)
                            {
                                tooltip.Close();
                            }
                        }
                        else if (__instance.IsBeingLoadedMagazine.Value || __instance.IsBeingUnloadedMagazine.Value)
                        {
                            __instance.ItemController.StopProcesses();
                        }
                        break;
                    }
                case PointerEventData.InputButton.Right:
                    __instance.ShowContextMenu(position);
                    break;
                case PointerEventData.InputButton.Middle:
                    if (!__instance.ExecuteMiddleClick())
                    {
                        newContextInteractions.ExecuteInteraction(EItemInfoButton.CheckMagazine);
                    }
                    break;
            }
            return false;
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryScreen), "TranslateCommand")]
        private static void HandleCloseInventoryPatch(InventoryScreen __instance, ECommand command)
        {
            if (!VRGlobals.inGame || !VRGlobals.vrPlayer)
                return;
            if (command.IsCommand(ECommand.Escape))
            {
                if (!__instance.Boolean_0)
                {
                    // If the menu is closed get rid of it, there would be better ways to do this but oh well 
                    UIPatches.HandleCloseInventory();
                }
            }
            if (command.IsCommand(ECommand.ToggleInventory))
            {
                UIPatches.HandleCloseInventory();
            }
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemsPanel), "Show")]
        private static void FixInventoryAfterRaid(ItemsPanel __instance, ItemContextAbstractClass sourceContext, CompoundItem lootItem, ISession session, InventoryController inventoryController, IHealthController health, Profile profile, InsuranceCompanyClass insurance, EquipmentBuildsStorageClass buildsStorage, EItemsTab currentTab, bool inRaid, bool isInventoryBlocked, [CanBeNull] InventoryEquipment equipment, Task __result)
        {
            __result.ContinueWith(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    if (lootItem is InventoryEquipment inventoryEquipment)
                    {
                        __instance._complexStashPanel.Show(__instance.inventoryController_0, sourceContext.CreateChild(inventoryEquipment), inventoryEquipment, __instance.profile_0.Skills, __instance.insuranceCompanyClass, __instance.itemUiContext_0);
                        __instance.UI.AddDisposable(__instance._complexStashPanel);
                        __instance.UI.AddDisposable(__instance._complexStashPanel.UnConfigure);

                    }
                    else if (lootItem != null)
                    {
                        __instance._simpleStashPanel.Show(lootItem, __instance.inventoryController_0, sourceContext.CreateChild(lootItem), inRaid, __instance.inventoryController_0, __instance.eitemsTab_0);
                        __instance.UI.AddDisposable(__instance._simpleStashPanel);
                    }
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

        }


        //-----------------------------------------------------------------------------------------------------------------
        // When in hideout the stash panel also gets shown which causes the UI to reposition/rotate so only rely
        // on this patch if its in raid, for hideout use PositionInHideoutInventory()
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GClass3521), "Show")]
        private static void PositionInRaidInventory(GClass3521 __instance)
        {
            // Dont open inv if not in game, player is in hideout, game player isn't set and the menu isn't already open
            if (!VRGlobals.inGame || VRGlobals.vrPlayer is HideoutVRPlayerManager || !VRGlobals.player || VRGlobals.menuOpen)
                return;

            UIPatches.HandleOpenInventory();

        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DraggedItemView), "OnDrag")]
        private static void PositionDraggedItemIcon(DraggedItemView __instance, PointerEventData eventData)
        {
            if (!(__instance.RectTransform_0 == null))
            {
                __instance.RectTransform_0.position = UIPatches.vrUiInteracter.uiPointerPos;
                __instance.RectTransform_0.localEulerAngles = VRGlobals.commonUi.eulerAngles;

            }
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SlotView), "Start")]
        private static void AddBoxColliderToSlotComponents(SlotView __instance)
        {
            if (!__instance.Transform.GetComponent<BoxCollider>())
                __instance.GameObject.AddComponent<BoxCollider>().extents = new Vector3(__instance.RectTransform.sizeDelta.x, __instance.RectTransform.sizeDelta.y, 1);
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static bool FixItemPlacement(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || !VRGlobals.inGame)
                return true;

            __instance.MovementContext?.AnimatorStatesLateUpdate();
            __instance.DistanceDirty = true;
            __instance.OcclusionDirty = true;
            if (__instance.HealthController != null && __instance.HealthController.IsAlive)
            {
                __instance.Physical.LateUpdate();
                __instance.VisualPass();
                __instance._armsupdated = false;
                __instance._bodyupdated = false;
                if (__instance._nFixedFrames > 0)
                {
                    __instance._nFixedFrames = 0;
                    __instance._fixedTime = 0f;
                }
                if (__instance._beaconDummy != null)
                {
                    //if (Physics.Raycast(new Ray(Camera.main.transform.position + Camera.main.transform.forward / 2f, Camera.main.transform.forward), out var hitInfo, 1.5f, LayerMaskClass.HighPolyWithTerrainMask))
                    if (Physics.Raycast(new Ray(VRGlobals.VRCam.transform.position + VRGlobals.VRCam.transform.forward / 2f, VRGlobals.VRCam.transform.forward), out var hitInfo, 1.5f, LayerMaskClass.HighPolyWithTerrainMask))
                    {
                        __instance._beaconDummy.transform.position = hitInfo.point;
                        __instance._beaconDummy.transform.rotation = Quaternion.LookRotation(hitInfo.normal);
                        __instance._beaconMaterialSetter.SetAvailable(__instance._beaconPlacer.Available);
                        __instance.AllowToPlantBeacon = __instance._beaconPlacer.Available;
                        if (__instance.AllowToPlantBeacon)
                        {
                            __instance.BeaconPosition = __instance._beaconDummy.transform.position;
                            __instance.BeaconRotation = __instance._beaconDummy.transform.rotation;
                        }
                    }
                    else
                    {
                        //__instance._beaconDummy.transform.position = Camera.main.transform.position + Camera.main.transform.forward;
                        __instance._beaconDummy.transform.position = VRGlobals.VRCam.transform.position + VRGlobals.VRCam.transform.forward;
                        __instance._beaconDummy.transform.rotation = Quaternion.identity;
                        __instance._beaconMaterialSetter.SetAvailable(isAvailable: false);
                        __instance.AllowToPlantBeacon = false;
                    }
                }
                __instance.ProceduralWeaponAnimation.StartFovCoroutine(__instance);
                __instance.PropUpdate();
            }
            __instance.ComplexLateUpdate(EUpdateQueue.Update, __instance.DeltaTime);
            if (__instance.POM != null && __instance.IsYourPlayer)
            {
                __instance.POM.ExtrudeCamera();
            }
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemView), "Update")]
        private static bool FixDragAndDropButton(ItemView __instance)
        {

            if (__instance != null && __instance.gameObject != null && __instance.pointerEventData_0 != null && (VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftTrigger.axis : SteamVR_Actions._default.RightTrigger.axis) < 0.7)
            {
                UIPatches.vrUiInteracter.EndDrop();
                __instance.OnEndDrag(__instance.pointerEventData_0);
            }
            if (__instance.IsSearched)
            {
                if (__instance.IsBeingDrained.Value)
                {
                    __instance.UpdateInfo();
                }
                if (Math.Abs(__instance.float_1 - __instance._mainImageAlpha) > 0.01f)
                {
                    __instance.float_1 = __instance._mainImageAlpha;
                    Color color = __instance.MainImage.color;
                    color.a = __instance._mainImageAlpha;
                    __instance.MainImage.color = color;
                }
            }
            return false;
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EquipItemWindow), "Show")]
        private static void PositionEquipItemWindow(EquipItemWindow __instance, Slot slot, InventoryController inventoryController, SkillManager skills, Vector3 position)
        {
            __instance.WindowTransform.localPosition = Vector3.zero;
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridView), "AcceptItem")]
        private static bool FixAcceptItem(GridView __instance, ItemContextClass itemContext, ItemContextAbstractClass targetItemContext, ref Task __result)
        {
            // Modify the flag argument based on your logic
            bool flag = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftGrip.state : SteamVR_Actions._default.RightGrip.state;

            // Call the original method with the modified flag
            __result = AcceptItemModified(__instance, itemContext, targetItemContext, flag);

            // Skip the original method
            return false;
        }


        //-----------------------------------------------------------------------------------------------------------------
        private static async Task AcceptItemModified(GridView __instance, ItemContextClass itemContext, ItemContextAbstractClass targetItemContext, bool flag)
        {
            // Your modified version of the AcceptItem method
            if (!__instance.CanAccept(itemContext, targetItemContext, out var operation) || !(await GClass3539.TryShowDestroyItemsDialog(operation.Value)))
            {
                return;
            }
            if (itemContext.Item is AmmoItemClass ammo)
            {
                Item item = __instance.method_8(targetItemContext);
                if (item != null)
                {
                    if (item is MagazineItemClass magazineItemClass)
                    {
                        MagazineItemClass magazineClass2 = magazineItemClass;
                        int loadCount = GridView.smethod_0(magazineClass2, ammo);
                        __instance._itemController.LoadMagazine(ammo, magazineClass2, loadCount).HandleExceptions();
                        return;
                    }
                    if (item is Weapon weapon)
                    {
                        Weapon weapon2 = weapon;
                        if (weapon2.SupportsInternalReload)
                        {
                            MagazineItemClass currentMagazine = weapon2.GetCurrentMagazine();
                            if (currentMagazine != null)
                            {
                                int num = GridView.smethod_0(currentMagazine, ammo);
                                if (num != 0)
                                {
                                    __instance._itemController.LoadWeaponWithAmmo(weapon2, ammo, num).HandleExceptions();
                                    return;
                                }
                            }
                        }
                        else
                        {
                            Weapon weapon3 = weapon;
                            if (weapon3.IsMultiBarrel)
                            {
                                int ammoCount = GridView.smethod_1(weapon3, ammo);
                                __instance._itemController.LoadMultiBarrelWeapon(weapon3, ammo, ammoCount).HandleExceptions();
                                return;
                            }
                        }
                    }
                }
            }
            if (!operation.Failed && __instance._itemController.CanExecute(operation.Value))
            {
                IRaiseEvents value = operation.Value;
                if (value == null)
                {
                    goto IL_0327;
                }
                if (!(value is GClass3216 gClass))
                {
                    if (!(value is GClass3217 gClass2))
                    {
                        goto IL_0327;
                    }
                    GClass3217 gClass3 = gClass2;
                    itemContext.DragCancelled();
                    if (gClass3.Count > 1 && flag)
                    {
                        __instance.itemUiContext_0.SplitDialog.Show(GClass2112.Localized("Transfer"), gClass3.Count, itemContext.CursorPosition, delegate (int count)
                        {
                            __instance.itemUiContext_0.SplitDialog.Hide();
                            __instance._itemController.TryRunNetworkTransaction(gClass3.ExecuteWithNewCount(count, simulate: true));
                        }, delegate
                        {
                            __instance.itemUiContext_0.SplitDialog.Hide();
                        });
                    }
                    else
                    {
                        __instance._itemController.RunNetworkTransaction(gClass3);
                    }
                }
                else
                {
                    GClass3216 gClass4 = gClass;
                    itemContext.DragCancelled();
                    if (gClass4.Count > 1 && flag)
                    {
                        __instance.itemUiContext_0.SplitDialog.Show(GClass2112.Localized("Split"), gClass4.Count, itemContext.CursorPosition, delegate (int count)
                        {
                            __instance.itemUiContext_0.SplitDialog.Hide();
                            gClass4.ExecuteWithNewCount(__instance._itemController, count);
                        }, delegate
                        {
                            __instance.itemUiContext_0.SplitDialog.Hide();
                        });
                    }
                    else
                    {
                        __instance._itemController.RunNetworkTransaction(gClass4);
                    }
                }
                goto IL_033e;
            }
            itemContext.DragCancelled();
            return;
        IL_0327:
            __instance._itemController.RunNetworkTransaction(operation.Value);
            goto IL_033e;
        IL_033e:
            ItemUiContext.PlayOperationSound(itemContext.Item, operation.Value);
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridView), "CanAccept")]
        private static bool FixCanAccept(GridView __instance, ItemContextClass itemContext, ItemContextAbstractClass targetItemContext, out GStruct454 operation, ref bool __result)
        {
            if (!__instance.SourceContext.DragAvailable)
            {
                operation = new GClass3790(itemContext.Item);
                return false;
            }
            operation = default(GStruct454);
            if (__instance.Grid == null)
            {
                return false;
            }
            if (__instance._nonInteractable)
            {
                return false;
            }
            Item item = itemContext.Item;
            LocationInGrid locationInGrid = __instance.CalculateItemLocation(itemContext);
            Item item2 = __instance.method_8(targetItemContext);
            GClass3186 gClass = __instance.Grid.CreateItemAddress(locationInGrid);
            ItemAddress itemAddress = itemContext.ItemAddress;
            if (itemAddress == null)
            {
                return false;
            }
            if (targetItemContext != null && !targetItemContext.ModificationAvailable)
            {
                operation = new StashGridClass.GClass3787(__instance.Grid);
                return false;
            }
            if (itemAddress.Container == __instance.Grid && __instance.Grid.GetItemLocation(item) == locationInGrid)
            {
                return false;
            }
            bool partialTransferOnly = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftGrip.state : SteamVR_Actions._default.RightGrip.state;
            if (item.CheckAction(gClass).Failed)
            {
                return false;
            }
            if (__instance.SourceContext.RotationLock.HasValue && itemContext.ItemRotation != __instance.SourceContext.RotationLock.Value)
            {
                return false;
            }
            operation = ((item2 != null) ? __instance._itemController.ExecutePossibleAction(itemContext, item2, partialTransferOnly, simulate: true) : __instance._itemController.ExecutePossibleAction(itemContext, __instance.SourceContext, gClass, partialTransferOnly, simulate: true));
            __result = operation.Succeeded;

            return false;
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SplitDialog), "Show", new Type[] { typeof(string), typeof(int), typeof(Vector2), typeof(Action<int>), typeof(Action), typeof(SplitDialog.ESplitDialogType) })]
        private static void RepositionSplitWindow(SplitDialog __instance)
        {

            __instance._window.localPosition = Vector3.zero;
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SplitDialog), "Show", new Type[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(Vector2), typeof(Action<int>), typeof(Action), typeof(SplitDialog.ESplitDialogType), typeof(bool), })]
        private static void RepositionConsumablesWindow(SplitDialog __instance)
        {

            __instance._window.localPosition = Vector3.zero;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DraggedItemView), "method_6")]
        private static bool RotateInventoryItem(DraggedItemView __instance)
        {
            if (!UIPatches.vrUiInteracter.rotated && UIPatches.vrUiInteracter.rightJoyTimeHeld > 0.125)
            {
                UIPatches.vrUiInteracter.rotated = true;
                __instance.method_2(__instance.ItemContext.ItemRotation == ItemRotation.Horizontal ? ItemRotation.Vertical : ItemRotation.Horizontal);
                if ((UnityEngine.Object)__instance.iContainer != null)
                {
                    __instance.iContainer.HighlightItemViewPosition(__instance.ItemContext, __instance.itemContextAbstractClass, preview: false);
                }
                Vector3 newRot = __instance._mainImage.transform.localEulerAngles;
                newRot.y = Quaternion.Inverse(__instance.transform.localRotation).eulerAngles.y;
                __instance._mainImage.transform.localEulerAngles = newRot;

            }

            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemInfoWindowLabels), "method_4")]
        private static bool ApplyItemDetailWindowViewerRotate(ItemInfoWindowLabels __instance, PointerEventData pointerData)
        {
            //float xRot = (MenuPatches.vrUiInteracter.pressPosition.x - MenuPatches.vrUiInteracter.uiPointerPos.x) * -1 * 10;
            //float yRot = (MenuPatches.vrUiInteracter.pressPosition.y - MenuPatches.vrUiInteracter.uiPointerPos.y) * -1 * 10;

            // Get the world positions
            Vector3 pressPosition = UIPatches.vrUiInteracter.pressPosition;
            Vector3 currentPointerPosition = UIPatches.vrUiInteracter.uiPointerPos;

            // Transform the world positions to the local space of the UI/object
            Vector3 localPressPosition = VRGlobals.preloaderUi.transform.InverseTransformPoint(pressPosition);
            Vector3 localCurrentPointerPosition = VRGlobals.preloaderUi.transform.InverseTransformPoint(currentPointerPosition);

            // Calculate the difference in local space, invert it to make it rotate the correction direction, then divide by 15 so it doesn't rotate so quickly
            float yRot = (localPressPosition.y - localCurrentPointerPosition.y) * -1;
            float xRot = (localPressPosition.x - localCurrentPointerPosition.x) * -1;
            __instance.weaponPreview_0.Rotate(xRot, yRot, 0f, 0f);

            UIPatches.vrUiInteracter.pressPosition = UIPatches.vrUiInteracter.uiPointerPos;
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIDragComponent), "UnityEngine.EventSystems.IDragHandler.OnDrag")]
        private static bool FixItemDetailWindowDrag(UIDragComponent __instance, PointerEventData eventData)
        {

            //StackTrace stackTrace = new StackTrace();
            //Plugin.MyLog.LogError("End dragging " + stackTrace.ToString());
            if (__instance.method_1())
            {
                //__instance._target.localPosition = eventData.position - __instance.vector2_0;
                __instance._target.position = eventData.worldPosition;
                Vector3 newPos = __instance._target.localPosition;
                if (__instance.name != "CharacteristicsPanel")
                {
                    //newPos.x -= ((RectTransform)__instance._target.transform).sizeDelta.x / 2;
                    newPos.y -= ((RectTransform)__instance._target.transform).sizeDelta.y / 2;
                }
                else
                {
                    newPos.x -= ((RectTransform)__instance._target.transform).sizeDelta.x / 2;
                    //newPos.x -= ((RectTransform)__instance._target.transform).sizeDelta.y / 2;
                }
                //newPos.z = 0f;
                __instance._target.localPosition = newPos;

            }
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridView), "CalculateItemLocation")]
        private static bool FixDragHighlighting(GridView __instance, ItemContextClass itemContext, ref LocationInGrid __result)
        {
            //Plugin.MyLog.LogWarning(itemContext.ItemPosition);
            RectTransform rectTransform = __instance.transform.RectTransform();
            Vector2 size = rectTransform.rect.size;
            Vector2 pivot = rectTransform.pivot;
            Vector2 vector = size * pivot;
            Vector2 cellSizes = itemContext.Item.CalculateCellSize();
            Vector3 pointerPos = UIPatches.vrUiInteracter.uiPointerPos;
            // The highlighted spaces place the corner on the pointer position so I need to move the pointer position
            // so it highlight from the center of the pointer, and in world space each grid item is about 0.084 away
            // from each other so use this to center it properly
            if (itemContext.ItemRotation == ItemRotation.Vertical)
            {
                float tempHolder = cellSizes.x;
                cellSizes.x = cellSizes.y;
                cellSizes.y = tempHolder;
            }
            //Vector2 should come out to an x,y value that represents the position in the grid
            Vector2 vector2 = rectTransform.InverseTransformPoint(pointerPos);
            //Plugin.MyLog.LogWarning( vector2 + "   |     " + pointerPos);

            vector2 += vector;
            XYCellSizeStruct gStruct = itemContext.Item.CalculateRotatedSize(itemContext.ItemRotation);
            vector2 /= 63f;
            vector2.y = __instance.Grid.GridHeight - vector2.y;
            vector2.y -= gStruct.Y;
            vector2.x = vector2.x - cellSizes.x / 2;
            vector2.y = vector2.y + cellSizes.y / 2;

            __result = new LocationInGrid(Mathf.Clamp(Mathf.RoundToInt(vector2.x), 0, __instance.Grid.GridWidth), Mathf.Clamp(Mathf.RoundToInt(vector2.y), 0, __instance.Grid.GridHeight), itemContext.ItemRotation);
            //Quaternion gridRotation = rectTransform.rotation;
            //vector2 = gridRotation * vector2;
            // a uiPointerPos of -3.4 and 1.5 on X left most grid and Y in the middle, seems to be working just fine
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SimpleContextMenu), "CorrectPosition")]
        private static bool FixContextMenuPositioning(SimpleContextMenu __instance)
        {
            if (!VRGlobals.inGame)
            {
                Vector3 newRootPos = __instance.Transform.position;
                newRootPos.z = 1f;
                __instance.Transform.position = newRootPos;
            }
            else
            {
                __instance.transform.position = UIPatches.vrUiInteracter.pressPosition;
            }


            Vector3 newPos = __instance.transform.localPosition;
            if (__instance.transform.parent.name == "InteractionButtonsContainer")
            {
                //newPos.z = 0;
                newPos.x = (__instance.transform.parent as RectTransform).sizeDelta.x;
            }
            __instance.WaitOneFrame(delegate {
                if (__instance.name == "ItemContextSubMenu(Clone)" && UIPatches.vrUiInteracter.hitObject && UIPatches.vrUiInteracter.hitObject.transform as RectTransform)
                {
                    __instance.transform.position = UIPatches.vrUiInteracter.hitObject.transform.position;
                    Vector3 newPos = __instance.transform.localPosition;
                    //newPos.z = 0;
                    newPos.y = newPos.y + (__instance.RectTransform.sizeDelta.y / 2);
                    newPos.x = (UIPatches.vrUiInteracter.hitObject.transform as RectTransform).sizeDelta.x;
                    if (__instance.transform.parent.name == "ItemInfoWindowTemplate(Clone)")
                        newPos.x = ((UIPatches.vrUiInteracter.hitObject.transform as RectTransform).sizeDelta.x / 2) * -1;

                    __instance.transform.localPosition = newPos;
                }
            });
            //newPos.z = 0;
            __instance.transform.localPosition = newPos;

            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        //possible cause of preloaderui moving
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemUiContext), "EditTag")]
        private static void RepositionMessageWindow(ItemUiContext __instance)
        {
            __instance._children[__instance._children.Count - 1].transform.localPosition = Vector3.zero;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StretchArea), "Init")]
        private static void DisableItemDisplayWindowStretchComponents(UIDragComponent __instance)
        {
            __instance.gameObject.SetActive(false);
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Some piece of code keeps repositioning this shit and I can't figure out what 
        // so just wait a frame and set it. This is for the delete/confirm windows
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DialogWindow<GClass3546>), "Show")]
        private static void DisplayDialogWindow(DialogWindow<GClass3546> __instance, string title, Action showCloseButton)
        {
            __instance.WaitOneFrame(delegate
            {
                __instance.WindowTransform.localPosition = Vector3.zero;
            });
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RepairWindow), "Show")]
        private static void DisplayDialogWindow(RepairWindow __instance)
        {
            __instance.WaitOneFrame(delegate
            {
                __instance.transform.localPosition = Vector3.zero;
            });
        }

    }
}