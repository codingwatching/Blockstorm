﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using EasyButtons;
using Managers;
using Managers.IO;
using Managers.Serializer;
using UnityEngine;
using UnityEngine.Serialization;
using VoxelEngine;

public enum CopyType
{
    Rotate180,
    MirrorX,
    MirrorZ
}

public enum CopyDirection
{
    X,
    Z
}

[Serializable]
public class CopyRule
{
    [Serializable]
    public struct RemappingRule
    {
        public string oldName, newName;
    }

    public Vector3Int from, to;
    public CopyType copyType;
    public RemappingRule[] remappingRules;
    public CopyDirection direction;

    public CopyRule(Vector3Int from, Vector3Int to, CopyType copyType, RemappingRule[] remappingRules,
        CopyDirection direction)
    {
        this.from = from;
        this.to = to;
        this.copyType = copyType;
        this.remappingRules = remappingRules;
        this.direction = direction;
    }
}

public class MapConverter : MonoBehaviour
{
    public string mapName = "";
    public CopyRule[] copyRules;
    public CopyRule.RemappingRule[] remappingRules;
    public GameObject cube;

    /*
     * Convert a map of cubes into a Map class instance and serialise it into a compact JSON file.
     * Instead of dumping the whole 3-dimensional array, which is not even possible, the map is converted
     * to a smaller list representation that only stores non-air blocks. 
     */
    [Button]
    private void Map2Voxel()
    {
        var blockTypesList = WorldManager.instance.blockTypes.ToList();
        var cubes = GameObject.FindWithTag("MapGenerator").GetComponentsInChildren<MeshRenderer>();
        var blockTypes = new List<byte>();
        var positionsX = new List<int>();
        var positionsY = new List<int>();
        var positionsZ = new List<int>();
        foreach (var cube in cubes)
        {
            var id = int.Parse(cube.material.mainTexture.name.Split('_')[1]) - 1;
            var blockType = blockTypesList.FindIndex(e => e.topID == id || e.bottomID == id || e.sideID == id);
            var posNorm = Vector3Int.FloorToInt(cube.transform.position + Vector3.one * 0.25f);
            blockTypes.Add((byte)blockType);
            positionsX.Add(posNorm.x);
            positionsY.Add(posNorm.y);
            positionsZ.Add(posNorm.z);
        }

        var minX = positionsX.Min();
        var minY = positionsY.Min();
        var minZ = positionsZ.Min();
        var maxX = positionsX.Max();
        var maxZ = positionsZ.Max();
        var blocksList = blockTypes.Select((blockType, i) =>
            new BlockEncoding(
                (short)(positionsX[i] - minX),
                (short)(positionsY[i] - minY + 1),
                (short)(positionsZ[i] - minZ),
                blockType)).ToList();
        var mapSize = new Vector3Int(maxX - minX + 1, Map.MaxHeight, maxZ - minZ + 1);
        // Apply remapping rules
        foreach (var block in blocksList)
        foreach (var remap in remappingRules)
            if (block.type == WorldManager.instance.BlockTypeIndex(remap.oldName))
                block.type = WorldManager.instance.BlockTypeIndex(remap.newName);
        // Add bottom bedrock layer
        for (var x = 0; x < mapSize.x; x++)
        for (var z = 0; z < mapSize.z; z++)
            blocksList.Add(new BlockEncoding((short)x, 0, (short)z, 1));
        // Apply copy rules
        foreach (var rule in copyRules)
        {
            if (rule.direction == CopyDirection.X)
                throw new ArgumentOutOfRangeException();
            mapSize.z += rule.to.z - rule.from.z - 1;
            var blocksToCopy = blocksList.Where(it =>
                it.x >= rule.from.x && it.x < rule.to.x && it.y >= rule.from.y && it.y < rule.to.y &&
                it.z >= rule.from.z && it.z < rule.to.z);
            var newBlocks = rule.copyType switch
            {
                CopyType.Rotate180 =>
                    blocksToCopy.Select(it =>
                        new BlockEncoding((short)(maxX - minX - it.x), it.y, (short)
                            (maxZ - minZ - 1 + rule.to.z - rule.from.z - it.z), it.type)).ToList(),
                CopyType.MirrorX =>
                    blocksToCopy.Select(it =>
                        new BlockEncoding((short)(maxX - minX - it.x), it.y, (short)
                            (it.z + maxZ - minZ - 1), it.type)).ToList(),
                CopyType.MirrorZ =>
                    blocksToCopy.Select(it =>
                        new BlockEncoding((short)(it.x), it.y, (short)
                            (it.z + maxZ - minZ - 1), it.type)).ToList(),
                _ => throw new ArgumentOutOfRangeException()
            };
            foreach (var block in newBlocks)
            foreach (var remap in rule.remappingRules)
                if (block.type == WorldManager.instance.BlockTypeIndex(remap.oldName))
                    block.type = WorldManager.instance.BlockTypeIndex(remap.newName);
            blocksList.AddRange(newBlocks);
        }

        var map = new Map(mapName, blocksList, mapSize);
        Map.Serializer.Serialize(map, "maps", map.name);
        print($"Map [{map.name}] saved successfully!");
    }

    /*
     * Convert a voxel into a cube map. This became necessary when I lost a good prefab of a map in
     * progress due to a mistake of mine. So I wrote the following code to retrieve it from the JSON
     * voxel serialised file that I had luckily saved.
     * This can be used to avoid storing large prefabs (~10x memory usage compared to the JSON serialised voxel version).
     */
    [Button]
    private void Voxel2Map()
    {
        var map = GameObject.FindWithTag("MapGenerator").transform;
        var blocks = WorldManager.instance.map.blocks;
        var size = WorldManager.instance.map.size;
        for (var y = 1; y < size.y; y++) // Ignoring the indestructible base
        for (var x = 0; x < size.x; x++)
        for (var z = 0; z < size.z; z++)
        {
            if (blocks[y, x, z] == 0)
                continue;
            var cubeGo = Instantiate(cube, map);
            cubeGo.transform.position = new Vector3(x, y, z) + Vector3.one * 0.5f;
            var textureId = WorldManager.instance.blockTypes[blocks[y, x, z]].topID;
            cubeGo.GetComponent<MeshRenderer>().material =
                Resources.Load($"Textures/texturepacks/blockade/Materials/blockade_{(textureId + 1):D1}") as Material;
        }
    }

    private enum Serializers
    {
        JsonSerializer,
        BinarySerializer
    }

    /*
     * Serialize the map currently loaded in the world manager using the provided serialization method.
     * This is used to convert JSON encoded maps to binary encoding, which uses much less disk space.
     */
    [Button]
    private void SerializeMap(Serializers serializer)
    {
        var map = WorldManager.instance.map;
        if (serializer is Serializers.BinarySerializer)
            BinarySerializer.Instance.Serialize(map, "maps", map.name);
        else if (serializer is Serializers.JsonSerializer)
            JsonSerializer.Instance.Serialize(map, "maps", map.name);
    }
}