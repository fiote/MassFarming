﻿using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace MassFarming
{
    [HarmonyPatch]
    public class MassPlant
    {
        private static FieldInfo m_noPlacementCostField = AccessTools.Field(typeof(Player), "m_noPlacementCost");
        private static FieldInfo m_placementGhostField = AccessTools.Field(typeof(Player), "m_placementGhost");
        private static FieldInfo m_buildPiecesField = AccessTools.Field(typeof(Player), "m_buildPieces");

        private static Vector3 placedPosition;
        private static Quaternion placedRotation;
        private static Piece placedPiece;
        private static bool placeSuccessful = false;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "PlacePiece")]
        public static void PlacePiecePostfix(Player __instance, ref bool __result, Piece piece)
        {
            placeSuccessful = __result;
            if (__result)
            {
                var placeGhost = (GameObject)m_placementGhostField.GetValue(__instance);
                placedPosition = placeGhost.transform.position;
                placedRotation = placeGhost.transform.rotation;
                placedPiece = piece;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        public static void UpdatePlacementPrefix(bool takeInput, float dt)
        {
            //Clear any previous place result
            placeSuccessful = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        public static void UpdatePlacementPostfix(Player __instance, bool takeInput, float dt)
        {
            if (!placeSuccessful)
            {
                //Ignore when the place didn't happen
                return;
            }

            var plant = placedPiece.gameObject.GetComponent<Plant>();
            if (!plant)
            {
                return;
            }

            if (!Input.GetKey(MassFarming.ControllerPickupHotkey.Value.MainKey) && !Input.GetKey(MassFarming.MassActionHotkey.Value.MainKey))
            {
                //Hotkey required
                return;
            }

            var heightmap = Heightmap.FindHeightmap(placedPosition);
            if (!heightmap)
            {
                return;
            }

            foreach (var newPos in BuildPlantingGridPositions(placedPosition, plant, placedRotation))
            {
                if (placedPiece.m_cultivatedGroundOnly && !heightmap.IsCultivated(newPos))
                {
                    continue;
                }

                if (placedPosition == newPos)
                {
                    //Trying to place around the origin point, so avoid placing a duplicate at the same location
                    continue;
                }

                var tool = __instance.GetRightItem();
                var hasStamina = MassFarming.IgnoreStamina.Value || __instance.HaveStamina(tool.m_shared.m_attack.m_attackStamina);

                if (!hasStamina)
                {
                    Hud.instance.StaminaBarNoStaminaFlash();
                    return;
                }

                var hasMats = (bool)m_noPlacementCostField.GetValue(__instance) || __instance.HaveRequirements(placedPiece, Player.RequirementMode.CanBuild);
                if (!hasMats)
                {
                    return;
                }

                if (!HasGrowSpace(newPos, placedPiece.gameObject))
                {
                    continue;
                }

                GameObject newPlaceObj = UnityEngine.Object.Instantiate(placedPiece.gameObject, newPos, placedRotation);
                Piece component = newPlaceObj.GetComponent<Piece>();
                if (component)
                {
                    component.SetCreator(__instance.GetPlayerID());
                }
                placedPiece.m_placeEffect.Create(newPos, placedRotation, newPlaceObj.transform);
                Game.instance.GetPlayerProfile().m_playerStats.m_builds++;

                __instance.ConsumeResources(placedPiece.m_resources, 0);
                if (!MassFarming.IgnoreStamina.Value)
                {
                    __instance.UseStamina(tool.m_shared.m_attack.m_attackStamina);
                }
                if (tool.m_shared.m_useDurability)
                {
                    tool.m_durability -= tool.m_shared.m_useDurabilityDrain;
                    if (tool.m_durability <= 0f)
                    {
                        return;
                    }
                }
            }
        }

        private static List<Vector3> BuildPlantingGridPositions(Vector3 originPos, Plant placedPlant, Quaternion rotation)
        {
            var plantRadius = placedPlant.m_growRadius * 2;
            int halfGrid = MassFarming.PlantGridSize.Value / 2;

            List<Vector3> gridPositions = new List<Vector3>(MassFarming.PlantGridSize.Value * MassFarming.PlantGridSize.Value);
            Vector3 left = rotation * Vector3.left * plantRadius;
            Vector3 forward = rotation * Vector3.forward * plantRadius;
            Vector3 gridOrigin = originPos - (forward * halfGrid) - (left * halfGrid);

            Vector3 newPos;
            for (var x = 0; x < MassFarming.PlantGridSize.Value; x++)
            {
                newPos = gridOrigin;
                for (var z = 0; z < MassFarming.PlantGridSize.Value; z++)
                {
                    newPos.y = ZoneSystem.instance.GetGroundHeight(newPos);
                    gridPositions.Add(newPos);
                    newPos += left;
                }
                gridOrigin += forward;
            }
            return gridPositions;
        }

        static int _plantSpaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
        private static bool HasGrowSpace(Vector3 newPos, GameObject go)
        {
            if (go.GetComponent<Plant>() is Plant placingPlant)
            {
                Collider[] nearbyObjects = Physics.OverlapSphere(newPos, placingPlant.m_growRadius, _plantSpaceMask);
                return nearbyObjects.Length == 0;
            }
            return true;
        }

        private static GameObject[] _myGhost = new GameObject[1];

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "SetupPlacementGhost")]
        public static void SetupPlacementGhostPostfix(Player __instance)
        {
            DestroyGhosts();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        public static void UpdatePlacementGhostPostfix(Player __instance, bool flashGuardStone)
        {
            var ghost = (GameObject)m_placementGhostField.GetValue(__instance);
            if(!ghost || !ghost.activeSelf)
            {
                SetGhostsActive(false);
                return;
            }

            if (!Input.GetKey(MassFarming.ControllerPickupHotkey.Value.MainKey) && !Input.GetKey(MassFarming.MassActionHotkey.Value.MainKey))
            {
                //Hotkey required
                SetGhostsActive(false);
                return;
            }

            var plant = ghost.GetComponent<Plant>();
            if (!plant)
            {
                SetGhostsActive(false);
                return;
            }

            if (!EnsureGhostsBuilt(__instance))
            {
                SetGhostsActive(false);
                return;
            }

            var positions = BuildPlantingGridPositions(ghost.transform.position, plant, ghost.transform.rotation);
            for (int i = 0; i < _myGhost.Length; i++)
            {
                if (ghost.transform.position == positions[i])
                {
                    //Trying to place around the origin point, so avoid placing a duplicate at the same location
                    _myGhost[i].SetActive(false);
                    continue;
                }

                _myGhost[i].transform.position = positions[i];
                _myGhost[i].transform.rotation = ghost.transform.rotation;
                _myGhost[i].SetActive(true);
            }
        }

        private static bool EnsureGhostsBuilt(Player player)
        {
            var requiredSize = MassFarming.PlantGridSize.Value * MassFarming.PlantGridSize.Value;
            bool needsRebuild = !_myGhost[0] || _myGhost.Length != requiredSize;
            if (needsRebuild) 
            {
                DestroyGhosts();

                if(_myGhost.Length != requiredSize)
                {
                    _myGhost = new GameObject[requiredSize];
                }

                if (m_buildPiecesField.GetValue(player) is PieceTable pieceTable && pieceTable.GetSelectedPrefab() is GameObject prefab)
                {
                    if (prefab.GetComponent<Piece>().m_repairPiece)
                    {
                        //Repair piece doesn't have ghost
                        return false;
                    }

                    for (int i = 0; i < _myGhost.Length; i++)
                    {
                        _myGhost[i] = SetupMyGhost(player, prefab);
                    }
                }
                else
                {
                    //No prefab, so don't need ghost (this probably shouldn't ever happen)
                    return false;
                }
            }

            return true;
        }

        private static void DestroyGhosts()
        {
            for (int i = 0; i < _myGhost.Length; i++)
            {
                if (_myGhost[i])
                {
                    UnityEngine.Object.Destroy(_myGhost[i]);
                    _myGhost[i] = null;
                }
            }
        }

        private static void SetGhostsActive(bool active)
        {
            foreach(var ghost in _myGhost) 
            {
                ghost?.SetActive(active);
            }
        }

        private static GameObject SetupMyGhost(Player player, GameObject prefab)
        {
            //bool enabled = false;
            //TerrainModifier componentInChildren = prefab.GetComponentInChildren<TerrainModifier>();
            //if ((bool)componentInChildren)
            //{
            //    enabled = componentInChildren.enabled;
            //    componentInChildren.enabled = false;
            //}

            ZNetView.m_forceDisableInit = true;
            var newGhost = UnityEngine.Object.Instantiate(prefab);
            ZNetView.m_forceDisableInit = false;
            newGhost.name = prefab.name;

            //if ((bool)componentInChildren)
            //{
            //    componentInChildren.enabled = enabled;
            //}

            foreach (Joint joint in newGhost.GetComponentsInChildren<Joint>())
            {
                UnityEngine.Object.Destroy(joint);
            }

            foreach (Rigidbody rigidBody in newGhost.GetComponentsInChildren<Rigidbody>())
            {
                UnityEngine.Object.Destroy(rigidBody);
            }

            //Collider[] colliders = newGhost.GetComponentsInChildren<Collider>();
            //foreach (Collider collider in colliders)
            //{
            //    if (((1 << collider.gameObject.layer) & m_placeRayMask) == 0)
            //    {
            //        ZLog.Log("Disabling " + collider.gameObject.name + "  " + LayerMask.LayerToName(collider.gameObject.layer));
            //        collider.enabled = false;
            //    }
            //}

            int layer = LayerMask.NameToLayer("ghost");
            foreach(var childTransform in newGhost.GetComponentsInChildren<Transform>())
            {
                childTransform.gameObject.layer = layer;
            }

            foreach(var terrainModifier in newGhost.GetComponentsInChildren<TerrainModifier>())
            { 
                UnityEngine.Object.Destroy(terrainModifier);
            }

            foreach (GuidePoint guidepoint in newGhost.GetComponentsInChildren<GuidePoint>())
            {
                UnityEngine.Object.Destroy(guidepoint);
            }

            Light[] componentsInChildren7 = newGhost.GetComponentsInChildren<Light>();
            foreach (Light v in componentsInChildren7)
            {
                UnityEngine.Object.Destroy(v);
            }

            //AudioSource[] componentsInChildren8 = newGhost.GetComponentsInChildren<AudioSource>();
            //for (int i = 0; i < componentsInChildren8.Length; i++)
            //{
            //    componentsInChildren8[i].enabled = false;
            //}

            //ZSFX[] componentsInChildren9 = newGhost.GetComponentsInChildren<ZSFX>();
            //foreach (ZSFX v1 in componentsInChildren9)
            //{
            //    v1.enabled = false;
            //}

            //Windmill componentInChildren2 = newGhost.GetComponentInChildren<Windmill>();
            //if ((bool)componentInChildren2)
            //{
            //    componentInChildren2.enabled = false;
            //}

            //ParticleSystem[] componentsInChildren10 = newGhost.GetComponentsInChildren<ParticleSystem>();
            //for (int i = 0; i < componentsInChildren10.Length; i++)
            //{
            //    componentsInChildren10[i].gameObject.SetActive(value: false);
            //}

            Transform ghostOnlyTransform = newGhost.transform.Find("_GhostOnly");
            if ((bool)ghostOnlyTransform)
            {
                ghostOnlyTransform.gameObject.SetActive(value: true);
            }

            newGhost.transform.position = player.transform.position;
            newGhost.transform.localScale = prefab.transform.localScale;
            foreach (MeshRenderer meshRenderer in newGhost.GetComponentsInChildren<MeshRenderer>())
            {
                if (!(meshRenderer.sharedMaterial == null))
                {
                    Material[] sharedMaterials = meshRenderer.sharedMaterials;
                    for (int j = 0; j < sharedMaterials.Length; j++)
                    {
                        Material material = new Material(sharedMaterials[j]);
                        material.SetFloat("_RippleDistance", 0f);
                        material.SetFloat("_ValueNoise", 0f);
                        sharedMaterials[j] = material;
                    }
                    meshRenderer.sharedMaterials = sharedMaterials;
                    meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                }
            }

            return newGhost;
        }
    }
}
