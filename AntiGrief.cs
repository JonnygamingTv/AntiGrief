using SDG.Unturned;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Steamworks;
using UnityEngine;
using Rocket.Unturned.Player;
using Rocket.Unturned.Chat;
using Rocket.Core;
using Rocket.Core.Plugins;
using Rocket.API;

using Logger = Rocket.Core.Logging.Logger;
using Rocket.API.Collections;
using Rocket.Unturned.Items;

namespace AntiGrief
{
    public class AntiGrief : RocketPlugin<AntiGriefConfig>
    {
        public AntiGrief Instance;
        private DateTime CurTime = DateTime.Now;
        public HashSet<ushort> _ItemDropDeniedList = new HashSet<ushort>();
        public HashSet<ushort> _ItemInvRestrictedList = new HashSet<ushort>();
        public HashSet<ushort> _SkipItemIDs = new HashSet<ushort>();
        public HashSet<ushort> _SkipElementIDs = new HashSet<ushort>();
        public HashSet<ushort> _SkipVehicleIDs = new HashSet<ushort>();

        protected override void Load()
        {
            Instance = this;
            Configuration.Save();

            Level.onPrePreLevelLoaded = OnPrePreLevelLoaded + Level.onPrePreLevelLoaded;
            BarricadeManager.onDamageBarricadeRequested += OnElementDamaged;
            StructureManager.onDamageStructureRequested += OnElementDamaged;
            if (Instance.Configuration.Instance.RestrictHarvesting)
                InteractableFarm.OnHarvestRequested_Global += OnHarvested;
            if (Configuration.Instance.VehicleCarjackOwnerGroupOnly)
                VehicleManager.onVehicleCarjacked += OnCarjacked;
            if (Instance.Configuration.Instance.EnableItemDropRestriction)
                ItemManager.onServerSpawningItemDrop += OnServerSpawningItemDrop;

            _ItemDropDeniedList = new HashSet<ushort>(Configuration.Instance.ItemDropDeniedList);
            _ItemInvRestrictedList = new HashSet<ushort>(Configuration.Instance.ItemInvRestrictedList);
            _SkipItemIDs = new HashSet<ushort>(Configuration.Instance.SkipItemIDs);
            _SkipElementIDs = new HashSet<ushort>(Configuration.Instance.SkipElementIDs);
            _SkipVehicleIDs = new HashSet<ushort>(Configuration.Instance.SkipVehicleIDs);
        }

        protected override void Unload()
        {
            Level.onPrePreLevelLoaded -= OnPrePreLevelLoaded;
            BarricadeManager.onDamageBarricadeRequested -= OnElementDamaged;
            StructureManager.onDamageStructureRequested -= OnElementDamaged;
            if (Instance.Configuration.Instance.RestrictHarvesting)
                InteractableFarm.OnHarvestRequested_Global -= OnHarvested;
            if (Configuration.Instance.VehicleCarjackOwnerGroupOnly)
                VehicleManager.onVehicleCarjacked -= OnCarjacked;
            if (Instance.Configuration.Instance.EnableItemDropRestriction)
                ItemManager.onServerSpawningItemDrop -= OnServerSpawningItemDrop;
        }

        private void OnServerSpawningItemDrop(Item item, ref Vector3 location, ref bool shouldAllow)
        {
            if (_ItemDropDeniedList.Contains(item.id))
                shouldAllow = false;
        }

        private void OnCarjacked(InteractableVehicle vehicle, Player instigatingPlayer, ref bool allow, ref Vector3 force, ref Vector3 torque)
        {
            SteamPlayerID spID = instigatingPlayer.channel.owner.playerID;
            if (!vehicle.isLocked || vehicle.lockedOwner == spID.steamID || vehicle.lockedGroup == spID.group)
                allow = true;
            else
                allow = false;
        }

        public void FixedUpdate()
        {
            if (Level.isLoaded && Provider.clients.Count > 0 && Instance.Configuration.Instance.EnableInvRestrictedItemCheck)
            {
                // begin restricted inv item check block.
                if ((DateTime.Now - CurTime).TotalSeconds > Instance.Configuration.Instance.CheckFrequency)
                {
                    CurTime = DateTime.Now;
                    for (int i = 0; i < Provider.clients.Count; i++)
                    {
                        UnturnedPlayer player = UnturnedPlayer.FromSteamPlayer(Provider.clients[i]);
                        if (player == null)
                            continue;
                        if (!player.HasPermission("ir.safe"))
                        {
                            for (byte page = 0; page < PlayerInventory.PAGES && player.Inventory.items != null && player.Inventory.items[page] != null; page++)
                            {
                                for (byte itemI = 0; itemI < player.Inventory.getItemCount(page); itemI++)
                                {
                                    ushort ItemID = player.Inventory.getItem(page, itemI).item.id;
                                    if (_ItemInvRestrictedList.Contains(ItemID))
                                    {
                                        ItemAsset itemAsset = UnturnedItems.GetItemAssetById(ItemID);
                                        if (itemAsset == null)
                                            continue;
                                        UnturnedChat.Say(player, Translate("antigrief_inv_restricted", itemAsset.itemName, itemAsset.id));
                                        player.Inventory.removeItem(page, itemI);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Translations
        public override TranslationList DefaultTranslations
        {
            get
            {
                return new TranslationList
                {
                    { "antigrief_harvest_blocked", "You're not allowed to Harvest this player's crops!" },
                    { "antigrief_inv_restricted", "Restricted Item has been removed from Inventory: {0}({1})" }
                };
            }
        }

        private void OnHarvested(InteractableFarm harvestable, SteamPlayer instigatorPlayer, ref bool shouldAllow)
        {
            BarricadeRegion region = BarricadeManager.regions.Cast<BarricadeRegion>().FirstOrDefault(c => c.drops.Any(d => d != null && d.interactable != null && d.interactable == harvestable));
            if (region == null) return;
            BarricadeDrop drop = region.drops.FirstOrDefault(c => c.interactable != null && c.interactable.transform == harvestable.transform);
            if (drop != null)
            {
                BarricadeData data = drop.GetServersideData();
                UnturnedPlayer instigatorUser = UnturnedPlayer.FromSteamPlayer(instigatorPlayer);
                if ((CSteamID)data.owner != instigatorUser.CSteamID && ((CSteamID)data.group != instigatorUser.Player.quests.groupID || data.group == 0) && !R.Permissions.HasPermission(instigatorUser, "antigrief.bypass"))
                {
                    if (Instance.Configuration.Instance.ShowHarvestBlockMessage)
                        UnturnedChat.Say(instigatorUser, Instance.Translate("antigrief_harvest_blocked"), Color.red);
                    shouldAllow = false;
                }
            }
        }

        private void OnElementDamaged(CSteamID instigatorSteamID, Transform elementTransform, ref ushort pendingTotalDamage, ref bool shouldAllow, EDamageOrigin damageOrigin)
        {
            switch (damageOrigin)
            {
                case EDamageOrigin.Flamable_Zombie_Explosion:
                case EDamageOrigin.Mega_Zombie_Boulder:
                case EDamageOrigin.Radioactive_Zombie_Explosion:
                case EDamageOrigin.Zombie_Electric_Shock:
                case EDamageOrigin.Zombie_Fire_Breath:
                case EDamageOrigin.Zombie_Stomp:
                case EDamageOrigin.Zombie_Swipe:
                    {
                        if (Instance.Configuration.Instance.DisableZombieElementDamage)
                            shouldAllow = false;
                        break;
                    }
                case EDamageOrigin.Punch:
                case EDamageOrigin.Vehicle_Bumper:
                case EDamageOrigin.Vehicle_Explosion:
                case EDamageOrigin.Food_Explosion:
                    {
                        if (Instance.Configuration.Instance.DisableMiscElementDamage)
                            shouldAllow = false;
                        break;
                    }
                case EDamageOrigin.Trap_Explosion:
                case EDamageOrigin.Trap_Wear_And_Tear:
                    {
                        if (Instance.Configuration.Instance.DisableZombieTrapDamage)
                            shouldAllow = false;
                        break;
                    }
            }
        }

        private void OnPrePreLevelLoaded(int level)
        {
            List<ItemAsset> AssetList = new List<ItemAsset>();
            Assets.find(AssetList);

            ushort gunsModified = 0;
            ushort meleesModified = 0;
            ushort throwablesModified = 0;
            ushort trapsModified = 0;
            ushort chargesModified = 0;
            ushort vehiclesModified = 0;
            ushort magsModified = 0;
            ushort elementsModified = 0;

            Logger.LogWarning("Starting anti grief modification run.");
            BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            bool shouldUpdateCount;
            for (int i = 0; i < AssetList.Count; i++)
            {
                shouldUpdateCount = false;
                Asset asset = AssetList[i];
                // Look for and skip id's in the skil lists.
                if (_SkipItemIDs.Contains(asset.id) || _SkipElementIDs.Contains(asset.id)) continue;

                // Run though updating the items/elements/vehicles on the server.
                if (asset is ItemWeaponAsset)
                {
                    ItemWeaponAsset weaponAsset = asset as ItemWeaponAsset;
                    // Start modifying weapon type bundles, but skip the blowtorch(76) as that heals structures.
                    if (weaponAsset.barricadeDamage > 0 && Configuration.Instance.NegateBarricadeDamage && weaponAsset.id != 76)
                    {
                        weaponAsset.barricadeDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (weaponAsset.structureDamage > 0 && Configuration.Instance.NegateStructureDamage && weaponAsset.id != 76)
                    {
                        weaponAsset.structureDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (weaponAsset.vehicleDamage > 0 && Configuration.Instance.NegateVehicleDamage && weaponAsset.id != 76)
                    {
                        weaponAsset.vehicleDamage = 0;
                        shouldUpdateCount = true;
                    }

                    if (weaponAsset.objectDamage > 0 && Configuration.Instance.NegateObjectDamage)
                    {
                        weaponAsset.objectDamage = 0;
                        shouldUpdateCount = true;
                    }
                    // Don't change resource damage for resource gathering weapons: Camp Axe(16), Fire Axe(104), Chain Saw(490), Pickaxe(1198), Jackhammer(1475).
                    if (weaponAsset.resourceDamage > 0 && Configuration.Instance.NegateResourceDamage && weaponAsset.id != 16 && weaponAsset.id != 104 && weaponAsset.id != 490 && weaponAsset.id != 1198 && weaponAsset.id != 1475)
                    {
                        weaponAsset.resourceDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (shouldUpdateCount)
                    {
                        if (weaponAsset is ItemGunAsset)
                            gunsModified++;
                        if (weaponAsset is ItemMeleeAsset)
                            meleesModified++;
                        if (weaponAsset is ItemThrowableAsset)
                            throwablesModified++;
                    }
                }
                else if (asset is ItemTrapAsset)
                {
                    ItemTrapAsset trapAsset = asset as ItemTrapAsset;
                    if (trapAsset.barricadeDamage > 0 && Configuration.Instance.NegateBarricadeDamage)
                    {
                        trapAsset.barricadeDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (trapAsset.structureDamage > 0 && Configuration.Instance.NegateStructureDamage)
                    {
                        trapAsset.structureDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (trapAsset.vehicleDamage > 0 && Configuration.Instance.NegateVehicleDamage)
                    {
                        trapAsset.vehicleDamage = 0;
                        shouldUpdateCount = true;
                    }

                    if (trapAsset.objectDamage > 0 && Configuration.Instance.NegateObjectDamage)
                    {
                        trapAsset.objectDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (trapAsset.resourceDamage > 0 && Configuration.Instance.NegateResourceDamage)
                    {
                        trapAsset.resourceDamage = 0;
                        shouldUpdateCount = true;
                    }

                    if (shouldUpdateCount)
                        trapsModified++;
                }
                else if (asset is ItemChargeAsset)
                {
                    ItemChargeAsset chargeAsset = asset as ItemChargeAsset;
                    if (chargeAsset.barricadeDamage > 0 && Configuration.Instance.NegateBarricadeDamage)
                    {
                        chargeAsset.barricadeDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (chargeAsset.structureDamage > 0 && Configuration.Instance.NegateStructureDamage)
                    {
                        chargeAsset.structureDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (chargeAsset.vehicleDamage > 0 && Configuration.Instance.NegateVehicleDamage)
                    {
                        chargeAsset.vehicleDamage = 0;
                        shouldUpdateCount = true;
                    }

                    if (chargeAsset.objectDamage > 0 && Configuration.Instance.NegateObjectDamage)
                    {
                        chargeAsset.objectDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (chargeAsset.resourceDamage > 0 && Configuration.Instance.NegateResourceDamage)
                    {
                        chargeAsset.resourceDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (shouldUpdateCount)
                        chargesModified++;
                }
                else if (asset is ItemMagazineAsset)
                {
                    ItemMagazineAsset magAsset = asset as ItemMagazineAsset;
                    if (magAsset.barricadeDamage > 0 && Configuration.Instance.NegateBarricadeDamage)
                    {
                        magAsset.barricadeDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (magAsset.structureDamage > 0 && Configuration.Instance.NegateStructureDamage)
                    {
                        magAsset.structureDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (magAsset.vehicleDamage > 0 && Configuration.Instance.NegateVehicleDamage)
                    {
                        magAsset.vehicleDamage = 0;
                        shouldUpdateCount = true;
                    }

                    if (magAsset.objectDamage > 0 && Configuration.Instance.NegateObjectDamage)
                    {
                        magAsset.objectDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (magAsset.resourceDamage > 0 && Configuration.Instance.NegateResourceDamage)
                    {
                        magAsset.resourceDamage = 0;
                        shouldUpdateCount = true;
                    }
                    if (shouldUpdateCount)
                        magsModified++;
                }
                shouldUpdateCount = false;
                if (asset is ItemBarricadeAsset)
                {
                    ItemBarricadeAsset basset = asset as ItemBarricadeAsset;
                    if (basset.health < Configuration.Instance.MinElementSpawnHealth && Configuration.Instance.ModifyMinElementSpawnHealth)
                    {
                        basset.GetType().GetField("_health", bindingFlags).SetValue(basset, Configuration.Instance.MinElementSpawnHealth);
                        shouldUpdateCount = true;
                    }
                    if (!basset.proofExplosion && Configuration.Instance.MakeElementsExplosionProof && (Configuration.Instance.MakeElementsExplosionProofIncludeTraps ? true : !(asset is ItemTrapAsset)))
                    {
                        basset.GetType().GetField("_proofExplosion", bindingFlags).SetValue(basset, true);
                        shouldUpdateCount = true;
                    }
                    if (basset.isVulnerable && Configuration.Instance.MakeElementsInvulnerable)
                    {
                        basset.GetType().GetField("_isVulnerable", bindingFlags).SetValue(basset, false);
                        shouldUpdateCount = true;
                    }
                    if ((basset.build == EBuild.SIGN || basset.build == EBuild.SIGN_WALL || basset.build == EBuild.NOTE) && !basset.isLocked && Configuration.Instance.MakeSignsLocked)
                    {
                        basset.GetType().GetField("_isLocked", bindingFlags).SetValue(basset, true);
                        shouldUpdateCount = true;
                    }
                }
                if (asset is ItemStructureAsset)
                {
                    ItemStructureAsset sasset = asset as ItemStructureAsset;
                    if (sasset.health < Configuration.Instance.MinElementSpawnHealth && Configuration.Instance.ModifyMinElementSpawnHealth)
                    {
                        sasset.GetType().GetField("_health", bindingFlags).SetValue(sasset, Configuration.Instance.MinElementSpawnHealth);
                        shouldUpdateCount = true;
                    }
                    if (!sasset.proofExplosion && Configuration.Instance.MakeElementsExplosionProof)
                    {
                        sasset.GetType().GetField("_proofExplosion", bindingFlags).SetValue(sasset, true);
                        shouldUpdateCount = true;
                    }
                    if (sasset.isVulnerable && Configuration.Instance.MakeElementsInvulnerable)
                    {
                        sasset.GetType().GetField("_isVulnerable", bindingFlags).SetValue(sasset , false);
                        shouldUpdateCount = true;
                    }
                }
                if (asset is ItemStorageAsset)
                {
                    ItemStorageAsset stasset = asset as ItemStorageAsset;
                    // make displays locked, or normal unlocked containers locked(excluding the airdrop container.).
                    if ((stasset.isDisplay && !stasset.isLocked && Configuration.Instance.MakeDisplaysLocked) || (!stasset.isLocked && Configuration.Instance.MakeContainersLocked && stasset.id != 1374))
                    {
                        stasset.GetType().GetField("_isLocked", bindingFlags).SetValue(stasset, true);
                        shouldUpdateCount = true;
                    }
                    if (stasset.isDisplay && Configuration.Instance.ModDisplayGrid)
                    {
                        if (stasset.storage_y < Configuration.Instance.DisplayGridY)
                        {
                            stasset.GetType().GetField("_storage_y", bindingFlags).SetValue(stasset, Configuration.Instance.DisplayGridY);
                            shouldUpdateCount = true;
                        }
                        if (stasset.storage_x < Configuration.Instance.DisplayGridX)
                        {
                            stasset.GetType().GetField("_storage_x", bindingFlags).SetValue(stasset, Configuration.Instance.DisplayGridX);
                            shouldUpdateCount = true;
                        }
                    }
                }
                if (shouldUpdateCount)
                    elementsModified++;
            }

            List<VehicleAsset> vehicleList = new List<VehicleAsset>();
            Assets.find(vehicleList);

            for (int v = 0; v < vehicleList.Count; v++)
            {
                shouldUpdateCount = false;
                Asset asset = vehicleList[v];
                if (_SkipVehicleIDs.Contains(asset.id)) continue;

                VehicleAsset vAsset = asset as VehicleAsset;
                if ((vAsset.isVulnerable || vAsset.isVulnerableToBumper || vAsset.isVulnerableToEnvironment || vAsset.isVulnerableToExplosions) && Configuration.Instance.MakeVehiclesInvulnerable)
                {
                    vAsset.isVulnerable = false;
                    vAsset.isVulnerableToBumper = false;
                    vAsset.isVulnerableToEnvironment = false;
                    vAsset.isVulnerableToExplosions = false;
                    shouldUpdateCount = true;
                }
                if (vAsset.isVulnerableToExplosions && Configuration.Instance.MakeVehiclesInvulnerableExplosions)
                {
                    vAsset.isVulnerableToExplosions = false;
                    shouldUpdateCount = true;
                }
                if (vAsset.canTiresBeDamaged && Configuration.Instance.MakeTiresInvulnerable)
                {
                    vAsset.canTiresBeDamaged = false;
                    shouldUpdateCount = true;
                }
                if (vAsset.healthMax < Configuration.Instance.MinVehicleSpawnHealth && Configuration.Instance.ModifyMinVehicleSpawnHealth)
                {
                    vAsset.GetType().GetField("_healthMax", bindingFlags).SetValue(vAsset, Configuration.Instance.MinVehicleSpawnHealth);
                    shouldUpdateCount = true;
                }
                if (vAsset.BuildablePlacementRule != EVehicleBuildablePlacementRule .AlwaysAllow && Configuration.Instance.VehicleSetMobileBuildables)
                {
                    vAsset.GetType().GetProperty("supportsMobileBuildables", bindingFlags | BindingFlags.Public).SetValue(vAsset, true, null);
                    // Bundle hash needs to be disabled for these, as this flag for this needs to be set client side as well.
                    vAsset.GetType().GetField("_shouldVerifyHash", bindingFlags).SetValue(vAsset, false);
                    shouldUpdateCount = true;
                }
                if (shouldUpdateCount)
                    vehiclesModified++;
            }
            Logger.LogWarning(string.Format("Finished modification run, counts of bundles modified: Guns: {0}, Mags: {6}, Melee: {1}, Throwables: {2}, Traps: {3}, Charges: {4}, Vehicles: {5}, Elements: {7}.", gunsModified, meleesModified, throwablesModified, trapsModified, chargesModified, vehiclesModified, magsModified, elementsModified));
        }
    }
}
