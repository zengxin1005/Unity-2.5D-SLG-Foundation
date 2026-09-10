/*
 * 文件名: WorldObject.cs
 * 作者: zengxin
 * 创建日期: 2026-8-15
 */

using UnityEngine;
public class WorldObject : MonoBehaviour
{
    public int TileX { get; set; }
    public int TileY { get; set; }
    
    
    public virtual void Recycle()
    {
     
    }
}

