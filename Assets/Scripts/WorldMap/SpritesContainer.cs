/*
 * 文件名: SpritesContainer.cs
 * 作者: zengxin
 * 创建日期: 2026-7-29
 */
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Scx;

[Serializable]
public class SpritesInfo
{
	public string name;
	public Sprite sprite;

	public SpritesInfo (string name, Sprite sprite)
	{
		this.name = name;
		this.sprite = sprite;
	}
}

public class SpritesContainer : MonoBehaviour
{
	public bool hasSetUp = false;
	public List<SpritesInfo> spriteList = new List<SpritesInfo> ();
	private Dictionary<string,Sprite> m_infoDic = new Dictionary<string, Sprite> ();

	public Sprite GetSpriteByName (string name)
	{
        Sprite sprite = null;
        m_infoDic.TryGetValue(name, out sprite);
        return sprite;
	}

	public SpritesContainer Setup ()
	{
		if (hasSetUp)
		{
			return this;
		}
		for (int i = 0; i < spriteList.Count; i++) {
            SpritesInfo si = spriteList [i];
            m_infoDic.Add(si.name, si.sprite);
		}
		hasSetUp = true;
		return this;
	}

	public void Clear ()
	{
        spriteList.Clear();
		m_infoDic.Clear ();
		hasSetUp = false;
	}
}
