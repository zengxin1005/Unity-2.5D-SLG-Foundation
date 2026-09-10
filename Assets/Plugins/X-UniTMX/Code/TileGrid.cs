/*! 
 * X-UniTMX: A tiled map editor file importer for Unity3d
 * https://bitbucket.org/Chaoseiro/x-unitmx
 * 
 * Copyright 2013-2014 Guilherme "Chaoseiro" Maia
 *           2014 Mario Madureira Fontes
 */
using UnityEngine;
using System.Collections.Generic;

namespace X_UniTMX
{
	/// <summary>
	/// A 2D grid of Tile objects.
	/// </summary>
	public class TileGrid
	{
		private readonly Tile[,] rawTiles;
        private Dictionary<int, Tile> rawTilesDict;
		/// <summary>
		/// Gets or sets a Tile at a given index.
		/// </summary>
		/// <param name="x">The X index.</param>
		/// <param name="y">The Y index.</param>
		/// <returns></returns>
		public Tile this[int x, int y]
		{
			get { return rawTiles[x, y]; }
			set { rawTiles[x, y] = value; }
		}

		/// <summary>
		/// Gets or sets a Tile at a given index.
		/// </summary>
		/// <param name="x">The X index.</param>
		/// <param name="y">The Y index.</param>
		/// <returns></returns>
		public Tile this[float x, float y]
		{
			get { return rawTiles[Mathf.FloorToInt(x), Mathf.FloorToInt(y)]; }
			set { rawTiles[Mathf.FloorToInt(x), Mathf.FloorToInt(y)] = value; }
		}

		/// <summary>
		/// Gets the width of the grid.
		/// </summary>
		public int Width { get; private set; }

		/// <summary>
		/// Gets the height of the grid.
		/// </summary>
		public int Height { get; private set; }

        public bool HasTile(int x, int y)
        {
            return rawTilesDict.ContainsKey(y * Width + x);
        }

        public void AddTile(int x, int y, Tile tile)
        {
            rawTilesDict.Add(y * Width + x, tile);
        }

        public Tile GetTile(int x, int y)
        {
            int key = y * Width + x;
            if(rawTilesDict.ContainsKey(key))
            {
                return rawTilesDict[key];
            }
            return null;
        }

        public Tile EraseTile(int x, int y)
        {
            int key = y * Width + x;
            if (rawTilesDict.ContainsKey(key))
            {
                Tile tile =  rawTilesDict[key];
                rawTilesDict.Remove(key);
                return tile;
            }
            return null;
        }

		/// <summary>
		/// Creates a new TileGrid.
		/// </summary>
		/// <param name="width">The width of the grid.</param>
		/// <param name="height">The height of the grid.</param>
		public TileGrid(int width, int height)
		{
			rawTiles = new Tile[width, height];
			Width = width;
			Height = height;
		}

        public TileGrid(int width, int height, bool makeAll)
        {
            if(makeAll)
                rawTiles = new Tile[width, height];
            Width = width;
            Height = height;
            rawTilesDict = new Dictionary<int, Tile>();
        }
	}
}
