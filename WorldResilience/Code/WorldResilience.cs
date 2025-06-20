﻿using HarmonyLib;
using NeoModLoader.api;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PholithMods
{
    public class WorldResilience : MonoBehaviour, IMod
    {
        public static GameObject backgroundAvatar;
        public static ModDeclare mod_declare;
        public static GameObject gameObject;
        private Harmony harmony;

        public ModDeclare GetDeclaration()
        {
            return mod_declare;
        }
        public GameObject GetGameObject()
        {
            return gameObject;
        }
        public string GetUrl()
        {
            return "https://github.com/WorldBoxOpenMods";
        }
        
        public void OnLoad(ModDeclare pModDecl, GameObject pGameObject)
        {
            mod_declare = pModDecl;
            gameObject = pGameObject;
            Config.isEditor = true;

            harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly()); // Pls NCMS do that automaticly...
        }

        public void OnUnload()
        {
            Harmony.UnpatchID(harmony.Id);
        }
    }

    public static class Utils
    {
        public static string Dump(this object o, Formatting formatting = Formatting.Indented)
        {
            return JsonConvert.SerializeObject(o, formatting, new JsonSerializerSettings()
            {
                NullValueHandling = NullValueHandling.Ignore,
            });
        }
        public static bool RespectConditionAround(this WorldTile tile, Func<WorldTile, bool> func, int minimumOfTilesThatMustRespect = 1)
        {
            int count = 0;
            foreach (WorldTile neighbor in tile.neighbours)
            {
                if (func.Invoke(neighbor))
                {
                    count++;
                    if (count >= minimumOfTilesThatMustRespect)
                    {
                        return true;
                    }
                }
            }
            return count >= minimumOfTilesThatMustRespect;
        }
        public static bool RespectConditionInDistance(this WorldTile tile, Func<WorldTile, bool> func, int distance = 2)
        {
            foreach (WorldTile neighbor in tile.neighbours)
            {
                if (!func.Invoke(neighbor) || (distance > 0 && !neighbor.RespectConditionInDistance(func, distance - 1)))
                {
                    return false;
                }
            }
            return true;
        }

        public static TileType DirtConvertionNoise(WorldTile tile)
        {
            return tile.TileNoise(4) > 0.5 ? TileLibrary.soil_low : TileLibrary.soil_high;
        }

        public static float TileNoise(this WorldTile tile, int sizeCoeff = 1)
        {
            return Mathf.PerlinNoise(((float)tile.x) / sizeCoeff, ((float)tile.y) / sizeCoeff);
        }
    }


    [HarmonyPatch(typeof(WorldBehaviourActionErosion), nameof(WorldBehaviourActionErosion.updateErosion))]
    public class WorldBehaviourActionErosion_updateErosion_patch
    {
        private static readonly object[] noArgument = new object[] { };
        private const int MAX_TILES_IN_LIST = 50;


        private static readonly Dictionary<WorldTile, TileType> dict = new Dictionary<WorldTile, TileType>();
        public static bool Prefix()
        {
            if (!WorldLawLibrary.world_law_erosion.isEnabled())
            {
                return false;
            }

            IslandsCalculator islandCalculator = Traverse.Create(MapBox.instance).Field<IslandsCalculator>("islands_calculator").Value;
            dict.Clear();
            islandCalculator.islands.ShuffleOne<TileIsland>();

            for (int i = 0; i < islandCalculator.islands.Count; i++)
            {
                TileIsland tileIsland = islandCalculator.islands[i];
                if (tileIsland.type == TileLayerType.Ground)
                {
                    for (int j = 0; j < MAX_TILES_IN_LIST * 3; j++) // *3 to fill better the list when conditions are rares
                    {

                        WorldTile randomTile = Traverse.Create(tileIsland).Method("getRandomTile", noArgument).GetValue<WorldTile>();
                        if (randomTile == null || dict.ContainsKey(randomTile))
                        {
                            continue;
                        }

                        // rocks to dirt
                        if (randomTile.Type.rocks
                            && Traverse.Create(randomTile).Method("IsOceanAround", noArgument).GetValue<bool>())
                        {
                            dict.Add(randomTile, TileLibrary.soil_high);
                            continue;
                        }
                        // Grass to sand
                        if ((randomTile.Type.can_be_biome || randomTile.Type.grass)
                            && Traverse.Create(randomTile).Method("IsOceanAround", noArgument).GetValue<bool>())
                        {
                            dict.Add(randomTile, TileLibrary.sand);
                            continue;
                        }

                        // sand to ocean
                        if (randomTile.Type.sand &&
                            randomTile.RespectConditionAround((otherTile) => otherTile.Type.ocean, 3))
                        {
                            dict.Add(randomTile, TileLibrary.shallow_waters);
                            continue;
                        }

                        // ocean to sand
                        foreach (WorldTile neighbor in randomTile.neighbours)
                        {
                            if ((neighbor.Type.ocean || neighbor.Type.can_be_filled_with_ocean)
                                && !dict.ContainsKey(neighbor)
                                && neighbor.RespectConditionAround((otherTile) => otherTile.Type.ground, 3))
                            {
                                dict.Add(neighbor, TileLibrary.sand);
                                break;
                            }

                            // ocean to sand expansion
                            if (neighbor.Type.ocean
                                && neighbor.Type.id == TileLibrary.shallow_waters.id
                                && neighbor.RespectConditionAround((otherTile) =>
                                    otherTile.Type.ground, neighbor.TileNoise(3) < 0.4 ? 1 : neighbor.TileNoise(3) < 0.85 ? 2 : 3)
                                && neighbor.RespectConditionInDistance((distanceNeighbor) =>
                                    // No deep ocean around
                                    !(distanceNeighbor.Type.ocean && (distanceNeighbor.Type.id == TileLibrary.close_ocean.id || distanceNeighbor.Type.id == TileLibrary.deep_ocean.id)), 1)
                                && !dict.ContainsKey(neighbor))
                            {
                                dict.Add(neighbor, TileLibrary.sand);
                                break;
                            }
                        }

                        // sand to dirt
                        if (randomTile.Type.sand
                            && randomTile.RespectConditionAround((otherTile) => otherTile.Type.layer_type == TileLayerType.Ground, 3) // At least 3 grounds near the sand
                            && (randomTile.RespectConditionInDistance((otherTile) => !otherTile.Type.ocean, 4)
                                || randomTile.RespectConditionAround((otherTile) => !otherTile.Type.sand, 3) && randomTile.RespectConditionAround((otherTile) => !otherTile.Type.ocean, 4)) // No ocean near the sand
                            && randomTile.RespectConditionAround((otherTile) => otherTile.Type.can_be_biome || otherTile.Type.grass, 1))
                        {

                            dict.Add(randomTile, Utils.DirtConvertionNoise(randomTile));
                            continue;
                        }

                        // wasteland to dirt
                        if (randomTile.Type.wasteland
                            && randomTile.RespectConditionAround((otherTile) => otherTile.Type.grass, 1) // minimum 1 grass
                            && randomTile.RespectConditionAround((otherTile) => otherTile.Type.layer_type == TileLayerType.Ground, 3))
                        {
                            dict.Add(randomTile, Utils.DirtConvertionNoise(randomTile));
                            continue;
                        }

                        // dirt to biome rarely
                        if (!randomTile.Type.is_biome && randomTile.Type.can_be_biome
                            && UnityEngine.Random.value < 0.001f
                            && randomTile.RespectConditionInDistance(
                                (otherTile) => !randomTile.Type.is_biome && randomTile.Type.can_be_biome))
                        {
                            TopTileType tile = BiomeLibrary.pool_biomes.GetRandom().getTile(randomTile);
                            MapAction.growGreens(randomTile, tile);
                            continue;
                        }


                        // If nothing of this happened, take another random tile, but in entire world this time.
                        // And check for ocean uniformisation
                        randomTile = Traverse.Create(MapBox.instance).Field<WorldTile[]>("tiles_list").Value.GetRandom();
                        if (randomTile == null || dict.ContainsKey(randomTile)) continue;

                        // transform ocean into shallow_water if near surface
                        bool mustBeShallowWater = randomTile.Type.ocean
                                && randomTile.RespectConditionAround((otherTile) => otherTile.Type.id == TileLibrary.shallow_waters.id
                                    || otherTile.Type.ground, randomTile.TileNoise(2) < 0.7 ? 1 : 2)
                                && !randomTile.RespectConditionInDistance((otherTile) => otherTile.Type.ocean, 3); // must not be at too much big distance from ground

                        if (randomTile.Type.id != TileLibrary.shallow_waters.id && mustBeShallowWater)
                        {
                            dict.Add(randomTile, TileLibrary.shallow_waters);
                            continue;
                        }
                        if (randomTile.Type.ocean && !mustBeShallowWater && randomTile.Type.id != TileLibrary.close_ocean.id
                            && randomTile.RespectConditionAround((otherTile) => otherTile.Type.id == TileLibrary.close_ocean.id, 3))
                        {
                            dict.Add(randomTile, TileLibrary.close_ocean);
                            continue;
                        }
                        if (randomTile.Type.ocean && !mustBeShallowWater
                            && randomTile.Type.id != TileLibrary.deep_ocean.id && randomTile.RespectConditionAround((otherTile) => otherTile.Type.id == TileLibrary.deep_ocean.id, 3))
                        {
                            dict.Add(randomTile, TileLibrary.deep_ocean);
                            continue;
                        }

                        // Ocean to deeper
                        if (randomTile.Type.id == TileLibrary.shallow_waters.id && !mustBeShallowWater)
                        {
                            dict.Add(randomTile, TileLibrary.close_ocean);
                            continue;
                        }
                        // Ocean to deeper // Cost too much
                        /*if (randomTile.Type.id == TileLibrary.close_ocean.id && randomTile.RespectConditionInDistance((otherTile) => !otherTile.Type.ground, 8))
                        {
                            dict.Add(randomTile, TileLibrary.deep_ocean);
                            continue;
                        }*/


                        if (dict.Count >= MAX_TILES_IN_LIST)
                        {
                            break;
                        }
                    }
                    if (dict.Count >= MAX_TILES_IN_LIST)
                    {
                        break;
                    }
                }
            }

            if (dict.Count == 0)
            {
                return false;
            }
            //dict.Keys.ShuffleOne();
            foreach (KeyValuePair<WorldTile, TileType> item in dict)
            {
                MapAction.terraformMain(item.Key, item.Value, AssetManager.terraform.get("flash"));
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(WorldLaws), nameof(WorldLaws.init))]
    public class WorldLaws_init_patch
    {
        [NonSerialized]
        public static PlayerOptionData world_regrow;

        public static void Postfix(WorldLaws __instance)
        {
            world_regrow = __instance.add(new PlayerOptionData("world_regrow")
            {
                boolVal = false
            });
        }
    }
}
