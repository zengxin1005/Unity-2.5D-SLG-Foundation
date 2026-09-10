/*
 * 文件名: MapMetaInfo.cs
 * 作者: zengxin
 * 创建日期: 2026-7-28
 */
using UnityEngine; 
public class MapMetaInfo
{
    #region 属性

    public int TileWidth { get; set; } = 254;
    public int TileHeight { get; set; } = 127;
    private int Margin { get;  set; } = 8; // 边缘的大小
    public int TileCountPerWidth { get; set; } = 601;  // 地图横向地块数量
    public int TileCountPerHeight { get; set; } = 601; // 地图纵向地块数量
    public int MarginWidthPixel { get; set; }
    public int MarginHeightPixel { get; set; }
    private int RowWithMargin { get; set; }
    
    private int ColHeightMargin { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    private Vector2 ContentSize { get; set; }
    #endregion

    public MapMetaInfo()
    {
        MarginWidthPixel = Margin * TileWidth;
        MarginHeightPixel = Margin * TileHeight;
        RowWithMargin = TileCountPerWidth + 2 * Margin;
        ColHeightMargin = TileCountPerHeight + 2 * Margin;
        PixelWidth = TileCountPerWidth * TileWidth;
        PixelHeight = TileCountPerHeight * TileHeight;
        ContentSize = new Vector2(TileWidth * TileCountPerWidth, TileHeight * TileCountPerHeight);
    }

    /// <summary>
    /// 设置Tiled地图
    /// </summary>
    public void SetTiledMap(X_UniTMX.Map tiledMap)
    {
        TileWidth = tiledMap.TileWidth;
        TileHeight = tiledMap.TileHeight;
        TileCountPerWidth = tiledMap.Width - 2 * Margin;
        TileCountPerHeight = tiledMap.Height - 2 * Margin;
        MarginWidthPixel = Margin * TileWidth;
        MarginHeightPixel = Margin * TileHeight;
        RowWithMargin = TileCountPerWidth + 2 * Margin;
        ColHeightMargin = TileCountPerHeight + 2 * Margin;
        PixelWidth = TileCountPerWidth * TileWidth;
        PixelHeight = TileCountPerHeight * TileHeight;
        ContentSize = new Vector2(TileWidth * TileCountPerWidth, TileHeight * TileCountPerHeight);
    }

    #region 坐标转换（带边缘）
    /// <summary>
    /// 瓦片坐标转带边缘的瓦片坐标
    /// </summary>
    public void TiledXYWithMargin(int xWithoutMargin, int yWithoutMargin, out int tileX, out int tileY)
    {
        tileX = xWithoutMargin + Margin;
        tileY = yWithoutMargin + Margin;
    }
    
    
    public void TiledXYWithoutMargin(int xWithMargin, int yWithMargin, out int tileX, out int tileY)
    {
        tileX = xWithMargin - Margin;
        tileY = yWithMargin - Margin;
    }

    /// <summary>
    /// XY坐标转带边缘的服务端索引
    /// </summary>
    public int TiledXYToIndexWithMargin(int xWithoutMargin, int yWithoutMargin)
    {
        int x = xWithoutMargin + Margin;
        int y = yWithoutMargin + Margin;
        return y * RowWithMargin + x;
    }

    /// <summary>
    /// 带边缘的服务端索引转XY坐标
    /// </summary>
    public void TiledIndexToXYWithMargin(int indexWithMargin, out int x, out int y)
    {
        int newIndex = indexWithMargin;
        x = newIndex % RowWithMargin;
        y = newIndex / RowWithMargin;
    }

    /// <summary>
    /// 判断无边缘坐标(xWithoutMargin, yWithoutMargin)是否落在带边缘地图合法范围内
    /// </summary>
    public bool IsTiledXYInRangeWithMargin(int xWithoutMargin, int yWithoutMargin)
    {
        int x = xWithoutMargin + Margin;
        int y = yWithoutMargin + Margin;
        return x >= 0 && x < RowWithMargin && y >= 0 && y < ColHeightMargin;
    }
    #endregion

    #region 坐标转换（无边缘）
    /// <summary>
    /// 瓦片坐标转世界坐标（假定地图世界坐标在0,0位置）
    /// </summary>
    public void TiledPositionToWorldPoint(int tileX, int tileY, out float worldX, out float worldY)
    {
        worldX = (TileWidth / 2.0f * (TileCountPerWidth - tileY + tileX)) / TileWidth;
        worldY = -TileCountPerHeight + TileHeight * (TileCountPerHeight - ((tileX + tileY) / (TileWidth / (float)TileHeight)) / 2.0f) / TileHeight;
    }

    /// <summary>
    /// 根据Content的世界坐标算出Tiled的世界坐标
    /// </summary>
    public void TiledPositionToWorldPointByContent(RectTransform content, int tileX, int tileY, out float worldX, out float worldY)
    {
        TiledPositionToContentPoint(tileX, tileY, out float pixelX, out float pixelY);
        Vector3 worldPos = content.TransformPoint(new Vector3(pixelX, pixelY, 0));
        worldX = worldPos.x;
        worldY = worldPos.y;
    }

    /// <summary>
    /// 根据瓦片坐标算出在Content中的像素坐标
    /// </summary>
    public void TiledPositionToContentPoint(int tileX, int tileY, out float pixelX, out float pixelY)
    {
        float x = TileWidth / 2.0f * (TileCountPerWidth + tileX - tileY - 1);
        float y = TileHeight / 2.0f * ((TileCountPerHeight * 2 - tileX - tileY) - 2);
        y = ContentSize.y - y;

        pixelX = x - ContentSize.x / 2 + TileWidth / 2.0f;
        pixelY = ContentSize.y / 2 - y + TileHeight;
    }

    /// <summary>
    /// Content中的像素坐标转瓦片坐标
    /// </summary>
    public void ContentPointToTiledPosition(float pixelX, float pixelY, out int tileX, out int tileY)
    {
        float x = pixelX + ContentSize.x / 2 - TileWidth / 2.0f;
        float y = ContentSize.y - (ContentSize.y / 2 - pixelY) - TileHeight;

        float fun1 = (2 * x / TileWidth - TileCountPerWidth + 1);
        float fun2 = (2 * y / TileHeight + 2) - TileCountPerHeight * 2;

        tileX = Mathf.FloorToInt((fun1 - fun2) / 2);
        tileY = Mathf.FloorToInt(-((fun1 + fun2) / 2));
    }

    /// <summary>
    /// 世界坐标转瓦片坐标（假定地图世界坐标在0,0位置）
    /// 与 TiledPositionToWorldPoint 严格互逆，由正变换直接反解：
    ///   worldX = (W - ty + tx) / 2        =>  tx - ty = 2*worldX - W
    ///   worldY = -(tx + ty) / (2*ratio)   =>  tx + ty = -2*ratio*worldY
    ///   （ratio = TileWidth / TileHeight）
    /// 取整用 FloorToInt（点落在哪个格子的区域就返回哪个格子），与 ContentPointToTiledPosition 一致
    /// </summary>
    public void WorldPointToTiledPosition(float worldX, float worldY, out int tileX, out int tileY)
    {
        float txMinusTy = 2 * worldX - TileCountPerWidth;  // tx - ty
        float txPlusTy = -2 * TileWidth / (float)TileHeight * worldY;              // tx + ty
        tileX = Mathf.FloorToInt((txMinusTy + txPlusTy) / 2);
        tileY = Mathf.FloorToInt((txPlusTy - txMinusTy) / 2);
    }
    #endregion

    #region 距离计算
    /// <summary>
    /// 计算两个瓦片坐标之间的Content距离
    /// 距离单位：100像素 = 1（向下取整）
    /// </summary>
    public int CalcDistance(int fromTileX, int fromTileY, int toTileX, int toTileY)
    {
        TiledPositionToContentPoint(fromTileX, fromTileY, out float fromPixelX, out float fromPixelY);
        TiledPositionToContentPoint(toTileX, toTileY, out float toPixelX, out float toPixelY);

        float deltaPixelX = toPixelX - fromPixelX;
        float deltaPixelY = toPixelY - fromPixelY;

        int distance = Mathf.FloorToInt(Mathf.Sqrt(deltaPixelX * deltaPixelX + deltaPixelY * deltaPixelY) / 100);
        return distance;
    }
    #endregion
}