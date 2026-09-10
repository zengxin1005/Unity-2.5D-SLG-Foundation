/*
 * 文件名: WorldMapView.cs
 * 作者: zengxin
 * 创建日期: 2026-9-5
 */
using System;
using Scx;
using UnityEngine;
using DG.Tweening;
using UnityEngine.UI;

public class WorldMapView:MonoBehaviour
{
    /// <summary>UI</summary>
    public InputField inputScroll;
    public Button btnScroll;
    public Text txtCenter;
    
    
    
    [SerializeField] private WorldMapController _controller;
    public WorldMiniMap miniMap;

    [Tooltip("城池详情面板根（锚底 stretch，隐藏时在屏幕下方；点击城池从底部滑出）")]
    public RectTransform cityPanel;
    [Tooltip("面板标题文本")]
    public Text txtCityTitle;
    [Tooltip("面板信息文本（城池 Id/坐标/关系 占位）")]
    public Text txtCityInfo;

    /// <summary>面板是否展开</summary>
    private bool _cityPanelOpen;
    /// <summary>面板滑入/滑出动画（生命周期内复用，切换/销毁时 Kill）</summary>
    private Tween _panelTween;
    /// <summary>面板滑出/滑入时长（秒）</summary>
    private const float PanelAnimDuration = 0.25f;

    private void Awake()
    {
        if (_controller == null)
            _controller = FindObjectOfType<WorldMapController>();
        if (_controller == null)
        {
            Debug.LogError("[WorldMapView] 场景中找不到 WorldMapController（确认 MapCanvas 节点存在且激活），城池面板/滚动联动将失效。");
            return;
        }
        _controller.MapView = this;
        txtCenter.text = string.Format("({0},{1})", _controller.CenterTileX, _controller.CenterTileY);  
        this.btnScroll.onClick.AddListener(() =>
        {
            if (!TryParseTileInput(inputScroll.text, out int tileX, out int tileY))
            {
                Debug.LogWarning($"[WorldMapView] 无法解析格子坐标: \"{inputScroll.text}\"，请输入 x,y 格式，例如 300,300");
                return;
            }
            _controller.ScrollToTile(tileX, tileY);
        });
    }

    /// <summary>
    /// 解析格子坐标输入，支持 "300,300"、"300 300"、"300，300"（中文逗号）等分隔写法
    /// </summary>
    private static bool TryParseTileInput(string text, out int tileX, out int tileY)
    {
        tileX = 0;
        tileY = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string normalized = text.Replace('，', ',').Replace('；', ';').Trim();
        string[] parts = normalized.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;

        return int.TryParse(parts[0].Trim(), out tileX) && int.TryParse(parts[1].Trim(), out tileY);
    }



    public  void SetMapCenter(int tileX,int tileY)
    {
        // 滚动/惯性期间 OnValueChanged 每帧调用：格号未变时跳过，避免每帧 string.Format 分配
        if (tileX == _lastCenterX && tileY == _lastCenterY)
            return;
        _lastCenterX = tileX;
        _lastCenterY = tileY;
        txtCenter.text = string.Format("({0},{1})", tileX, tileY);
    }

    private int _lastCenterX = int.MinValue;
    private int _lastCenterY = int.MinValue;

    #region 城池详情面板（WorldMapController 点击回调驱动）

    /// <summary>
    /// 地图轻点结果（WorldMapController.OnMapClick 回调，仅"释放且未拖动"时触发）。
    /// city != null：点中城池 → 刷新面板 + 滚动让位（城停在扣除底部面板后的可视区中心，偏上）。
    ///              此时面板通常已在按下瞬间被回收，这里负责"收旧→弹新"。
    /// city == null：点空白 → 兜底收起（面板已在按下瞬间回收，此分支幂等无操作）。
    /// </summary>
    public void OnMapClicked(ICityEntity city)
    {
        if (city == null)
        {
            CloseCityPanelOnly();
            return;
        }

        RefreshCityText(city);

        // 让位滚动：城停在"扣除底部面板后的剩余可视区中心"（= 屏幕中心上方 面板遮挡高的一半）。
        // 换算分两步，全程用真实投影几何，不假设"视口 rect 高 = 屏幕高"（相机实际取景范围与视口并不重合）：
        //   1) 屏幕像素：城应上移 liftScreenPx = r × Screen.height / 2（r = 面板高 / View 高，同为 UI 坐标）；
        //   2) 屏幕像素 → 视口局部单位：把视口四角投到屏幕，量出"视口 rect 高 = 多少屏幕像素"，
        //      换算系数自动消化相机 FOV/俯角/Canvas 缩放/视口与屏幕不重合等全部几何差异。
        if (_controller != null)
        {
            GestureScrollRect scroller = _controller.mapScroller;
            float panelH = cityPanel != null ? cityPanel.rect.height : 500f;
            float viewH = ((RectTransform)transform).rect.height;
            float r = viewH > 1f ? panelH / viewH : 0.3f;
            float liftScreenPx = r * Screen.height * 0.5f;

            Camera cam = null;
            Canvas mapCanvas = scroller.GetComponentInParent<Canvas>(); // 地图 Canvas（World Space）
            if (mapCanvas != null)
                cam = mapCanvas.worldCamera;
            if (cam == null) cam = Camera.main;
            Vector3[] corners = new Vector3[4];
            scroller.viewport.GetWorldCorners(corners);
            Vector2 sTop = RectTransformUtility.WorldToScreenPoint(cam, corners[1]);    // 视口上边
            Vector2 sBottom = RectTransformUtility.WorldToScreenPoint(cam, corners[3]); // 视口下边
            float vpScreenH = Mathf.Abs(sTop.y - sBottom.y);

            float offsetContent = 0f;
            if (vpScreenH > 1f)
            {
                float liftLocal = liftScreenPx / vpScreenH * scroller.viewport.rect.height; // 视口局部单位
                offsetContent = liftLocal / scroller.content.localScale.x;                  // → content 像素
            }
            _controller.ScrollToTile(city.TileX, city.TileY, true, offsetContent);
        }

        OpenCityPanel();
    }

    /// <summary>
    /// 用户在地图上按下 / 滚轮缩放（WorldMapController 转发）。
    /// 语义：只要在地图上"按下"就立即回收面板（不等待释放或拖动判定），地图位置保持不动。
    /// 若这次操作最终是轻点且命中了城池，会再走 OnMapClicked 重新弹出（收旧→弹新）。幂等。
    /// </summary>
    public void OnMapPressDown()
    {
        CloseCityPanelOnly();
    }

    /// <summary>收起面板（仅收 UI，不动地图）：滑出到底后隐藏。幂等。</summary>
    private void CloseCityPanelOnly()
    {
        if (!_cityPanelOpen || cityPanel == null)
            return;
        _cityPanelOpen = false;
        _panelTween?.Kill();
        _panelTween = cityPanel.DOAnchorPosY(-cityPanel.rect.height, PanelAnimDuration)
            .SetEase(Ease.OutCubic)
            .OnComplete(() => cityPanel.gameObject.SetActive(false));
    }

    /// <summary>展开面板：激活并复位到屏下后从底部滑入。幂等（已展开则不重复动画）。</summary>
    private void OpenCityPanel()
    {
        if (_cityPanelOpen || cityPanel == null)
            return;
        _cityPanelOpen = true;
        _panelTween?.Kill();

        cityPanel.gameObject.SetActive(true);
        // 复位到屏幕正下方再滑入（防止历史位置残留导致首帧闪现）
        cityPanel.anchoredPosition = new Vector2(cityPanel.anchoredPosition.x, -cityPanel.rect.height);
        _panelTween = cityPanel.DOAnchorPosY(0f, PanelAnimDuration).SetEase(Ease.OutCubic);
    }

    /// <summary>刷新面板文本（占位内容，接入真实城池配置后替换）</summary>
    private void RefreshCityText(ICityEntity city)
    {
        if (txtCityTitle != null)
            txtCityTitle.text = $"城池 Lv.{city.Type}";
        if (txtCityInfo != null)
        {
            string rel = city.Relation switch
            {
                MarchRelation.Self => "自己",
                MarchRelation.Ally => "盟友",
                MarchRelation.Neutral => "中立",
                MarchRelation.Enemy => "敌人",
                _ => city.Relation.ToString(),
            };
            txtCityInfo.text =
                $"ID：{city.Id}\n" +
                $"坐标：({city.TileX}, {city.TileY})\n" +
                $"关系：{rel}";
        }
    }

    #endregion

    void OnDestroy()
    {
        _panelTween?.Kill();
        _panelTween = null;
    }
}
