/*
 * 文件名: GestureScrollRect.cs
 * 创建日期: 2026-7-29
 * 作者: zengxin
 */
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using DG.Tweening;

namespace Scx
{
    public class GestureScrollRect : ScrollRect, IPointerClickHandler, IPointerDownHandler
    {
        public delegate bool OnValueChangedDelegate();
        public delegate void OnScrollToCenterDelegate();
        public delegate void OnScaleChangeDelegate();
        public delegate void OnDragBeginDelegate();
        public delegate void OnDragEndDelegate();
        public delegate void OnClickDelegate(float x, float y);
        public delegate void OnPressDownDelegate();


        [Header("缩放范围")]
        public float MinScale = 0.6f; // 手势最小可缩放比例
        public float MaxScale = 1f; // 手势最大可缩放比例

        [Header("滚轮 / 双指缩放")]
        public float ScrollWheelStep = 0.03f; // 滚轮每次缩放步进（仅编辑器滚轮）
        public float ScaleRatio = 0.7f;       // 双指缩放灵敏度（1 = 距离变化 1:1 映射）
        public float PinchThreshold = 10f;    // 双指距离变化小于此值不触发缩放

        [Header("交互")]
        public bool CanDrag = true;       // 是否允许拖动/缩放
        public bool ClickEnabled = true;  // 是否响应点击回调
        
        public float ScrollAnimateDuration = 0.2f;   // 居中滚动动画时长（秒）

        public OnValueChangedDelegate OnValueChanged { set; get; }
        public OnScrollToCenterDelegate OnScrollToCenter { set; get; }
        public OnScaleChangeDelegate OnScaleChange { set; get; }
        public OnDragBeginDelegate OnDragBegin { set; get; }
        public OnDragEndDelegate OnDragEnd { set; get; }
        public OnClickDelegate OnClick { set; get; }
        public OnPressDownDelegate OnPressDown { set; get; }

        // ---- 手势状态 ----
        private readonly List<PointerEventData> _touches = new List<PointerEventData>();
        private bool _dragging;
        private float _beginScale = 1.0f;      // 本次手势开始的缩放
        private float _beginDistance;    // 双指起始距离

        // 不随缩放变化的内容锚点基准 = anchoredPosition / localScale。
        // 几何意义：保持"视口中心钉住同一内容点"的不变量（中心对应 content 本地点 = -anchor，与 scale 无关）。
        // 惰性基准：拖动/动画/回滚期间不维护；所有缩放入口（滚轮、双指）使用前必须先重新同步
        private Vector2 _anchorPositionWithoutScale;

        private Vector2 _lastLocalPos = Vector2.zero;   // 上一次合法位置（越界回滚用）
        private Tween _moveCenterTween;          // 进行中的跳转动画

        /// <summary>
        /// 当前内容缩放值（安全取值）：localScale.x 为 0 时做除法会产生 Infinity/NaN，
        /// 所有"anchoredPosition / localScale"换算统一走这里，异常时退化为 1
        /// </summary>
        private float SafeScaleX
        {
            get
            {
                float s = content != null ? content.localScale.x : 1f;
                return Mathf.Abs(s) < 0.0001f ? 1f : s;
            }
        }

        protected override void Awake()
        {
            base.Awake();
            
            onValueChanged.AddListener((Vector2 pos) =>
            {
                if (null != OnValueChanged)
                {
                    bool isValid = OnValueChanged();
                    if (!isValid)
                    {
                        content.localPosition = _lastLocalPos;
                        StopAllMovement();
                    }
                    else
                    {
                        _lastLocalPos = content.localPosition;
                    }
                }
            });
        }

        protected override void Start()
        {
            base.Start();
            _anchorPositionWithoutScale = content.anchoredPosition / SafeScaleX;
            
            SetScale(content.localScale.x);
        }

        /// <summary>
        /// 隐藏/销毁时刹停：基类 OnDisable 只清惯性 velocity，不管我们的跳转补间。
        /// 不杀的话补间会继续写 inactive/销毁的 content——轻则白跑，重则下次打开时
        /// 残留补间把刚初始化的地图再挪一段，与 SetScale/初始定位打架。
        /// </summary>
        protected override void OnDisable()
        {
            base.OnDisable();
            KillMoveTween();
            // 清空手势状态：禁用期间触摸事件不会走 OnEndDrag（如切页/弹窗遮罩），
            // 残留的 PointerEventData 会让下次手势误判为双指（第 2 指永远进不了缩放态）
            _touches.Clear();
            _dragging = false;
        }

        /// <summary>
        /// 应用切后台/来电中断：系统不会补发 OnEndDrag/OnPointerUp，
        /// 双指捏合中途被打断会残留脏手势状态，切回后地图表现为"点不动/缩放态错乱"。
        /// 暂停时整体复位（刹停 + 清触摸 + 复位拖动标记），恢复后由下次按下重新驱动。
        /// </summary>
        public void OnApplicationPause(bool pause)
        {
            if (pause)
            {
                StopAllMovement();
                _touches.Clear();
                _dragging = false;
            }
        }

        #region 手势拖动与缩放

        public override void OnBeginDrag(PointerEventData eventData)
        {
            if (_touches.Count == 0)
            {
                base.OnBeginDrag(eventData);
            }
            // 打断进行中的跳转动画
            KillMoveTween();
            // 最多支持双指：第 3 根手指加入时直接忽略（>= 2 判断必须在 Add 之前，
            // 否则第 3 指进入列表会破坏双指缩放状态）
            if (_touches.Count >= 2)
            {
                return;
            }
            if (!_touches.Contains(eventData))
            {
                _touches.Add(eventData);
            }
            if (_touches.Count == 1)
            {
                _dragging = true; // 单指拖动
                _beginDistance = 0.0f;
            }
            else if (_touches.Count == 2)
            {
                // 单指拖动切换为双指缩放：结束 ScrollRect 内部拖动状态。
                // 若不结束，第一根手指抬起时不会经过 base.OnEndDrag，内部 m_Dragging 残留 true
                if (_dragging)
                {
                    base.OnEndDrag(_touches[0]);
                }
                // 清掉单指拖动残留的惯性速度，避免双指缩放期间内容继续漂移
                StopMovement();
                Vector2 p1 = _touches[0].position;
                Vector2 p2 = _touches[1].position;
                _beginDistance = Vector2.Distance(p1, p2);
                _anchorPositionWithoutScale = content.anchoredPosition / SafeScaleX;
                _beginScale = content.localScale.x;
                _dragging = false; // 双指缩放
            }

            if (null != OnDragBegin)
                OnDragBegin();
        }

        public override void OnDrag(PointerEventData eventData)
        {
            if (!_touches.Contains(eventData))
            {
                return;
            }
            if (_touches.Count == 1 && _dragging)
            {
                base.OnDrag(eventData);
            }
            else if (_touches.Count == 2 && !_dragging)
            {
                Vector2 p1 = _touches[0].position;
                Vector2 p2 = _touches[1].position;
                float dis = Vector2.Distance(p1, p2);

                if (Mathf.Abs(dis - _beginDistance) <= PinchThreshold)
                    return;

                // 双指起始距离为 0（异常输入）时按 1:1 处理，避免除零出 Infinity
                float scale = _beginDistance > 0.0001f ? dis / _beginDistance : 1f;
                // 用开始的缩放值 + (现有手势距离比例 - 1) * 灵敏度 = 最终需要的缩放值
                ProcessScale(_beginScale + (scale - 1.0f) * ScaleRatio);
            }
        }

        public override void OnEndDrag(PointerEventData eventData)
        {
            if (!_touches.Contains(eventData))
            {
                return;
            }
            if (_dragging)
            {
                base.OnEndDrag(eventData);
            }
            _touches.Remove(eventData);
            if (_touches.Count == 0)
            {
                _dragging = false;
                if (null != OnDragEnd)
                    OnDragEnd();
            }
        }

        /// <summary>
        /// 指针在地图上按下即触发（不关心之后是拖动还是释放）。
        /// 供"按下即反馈"场景使用：如城池面板展开期间，地图任意位置一按下立即回收面板。
        /// 按下同时刹停一切运动（ScrollToCenter 补间/惯性）——"手指一接触即接管地图"：
        ///   1) 让位滚动飞行中快速连点其它城 → 地图不再继续漂移，释放点命中坐标不再被滑动带偏；
        ///   2) 双指缩放的第一指按下即停掉飞行中的补间，避免补间终点(旧缩放)与缩放在半路打架。
        /// 共享组件，未订阅 OnPressDown 的界面同样受益（点按打断自动居中跳转是通用期望）。
        /// </summary>
        public void OnPointerDown(PointerEventData eventData)
        {
            StopAllMovement();
            if (null != OnPressDown)
                OnPressDown();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!ClickEnabled || _touches.Count > 0)
            {
                return;
            }

            StopAllMovement();
            if (null != OnClick)
            {
                Camera eventCamera = eventData.enterEventCamera;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(content, eventData.position, eventCamera, out Vector2 localPos);
                OnClick(localPos.x, localPos.y);
            }
        }

        #endregion

        #region 缩放

        /// <summary>
        /// 将缩放值 clamp 到 [MinScale, MaxScale] 后应用（带防抖）
        /// </summary>
        private void ProcessScale(float scale)
        {
            int diff = (int)(Mathf.Abs(scale - content.localScale.x) * 100);
            // 变化 < 0.02 视为防抖跳过；但接近边界时必须强制执行，
            // 否则浮点误差会导致缩放停在 MinScale/MaxScale 外约 0.02 处，进不了精确边界
            if (diff <= 1 && scale > MinScale + 0.001f && scale < MaxScale - 0.001f)
                return;
            SetScale(scale);
        }

        public void SetScale(float scale)
        {
            if (scale < MinScale)
            {
                scale = MinScale;
            }
            else if (scale > MaxScale)
            {
                scale = MaxScale;
            }
            // 缩放 = 重新锚定内容，作废飞行中的跳转补间（防止外部在补间途中直接调 SetScale 与其打架）。
            // 双指 pinch 每帧走这里时补间早被 OnPointerDown 刹停，Kill 为空操作，零成本。
            KillMoveTween();
            content.localScale = new Vector3(scale, scale, content.localScale.z);
            content.anchoredPosition = AdjustAnchoredPosition(scale, _anchorPositionWithoutScale);
            // 缩放产生的新位置已过 AdjustAnchoredPosition 边界钳制，属于合法位置：
            // 同步更新回滚基准，避免双指缩放后首次拖动若越界，回滚跳回缩放前的旧位置
            _lastLocalPos = content.localPosition;
            if (null != OnScaleChange)
                OnScaleChange();
        }

        /// <summary>
        /// 将"无缩放锚点"乘以目标缩放并 clamp 到内容边界内
        /// </summary>
        private Vector2 AdjustAnchoredPosition(float scale, Vector2 anchorPositionWithoutScale)
        {
            Vector2 pos = anchorPositionWithoutScale * scale;
            if (content == null || viewport == null)
            {
                return pos;
            }
            float maxX = (content.rect.width * scale - viewport.rect.width) / 2;
            float maxY = (content.rect.height * scale - viewport.rect.height) / 2;
            // 内容缩放后小于视口（maxX/maxY 为负）：clamp 边界反转会错误地把内容
            // 锁到任意位置，此时不做边界限制，交由外部控制（地图场景不会触发）
            if (maxX < 0 || maxY < 0)
            {
                return pos;
            }
            float minX = -maxX;
            float minY = -maxY;

            if (pos.x >= maxX)
                pos.x = maxX;
            else if (pos.x <= minX)
                pos.x = minX;

            if (pos.y >= maxY)
                pos.y = maxY;
            else if (pos.y <= minY)
                pos.y = minY;

            return pos;
        }

        private void Update()
        {
            if (!CanDrag) return;
#if UNITY_EDITOR
            // 滚轮缩放：取一次轴向，非零按方向步进（>0 放大 / <0 缩小）
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 0.0001f)
            {
                StopAllMovement();
                _anchorPositionWithoutScale = content.anchoredPosition / SafeScaleX;
                _beginScale = content.localScale.x;
                ProcessScale(_beginScale + Mathf.Sign(wheel) * ScrollWheelStep);
            }
#endif
        }

        #endregion

        #region 滚动与定位

        /// <summary>
        /// 视口左上角（viewport pivot = (0,1)）在 Content 本地坐标系中的像素坐标，
        /// 调用方（如 WorldMapController.OnValueChanged）按"可见区左上角"使用：+W/2 / -H/2 得到中心。
        /// 直接用 InverseTransformPoint 做世界→本地变换，与相机无关（Camera 未赋值/目标在视锥外也稳定）
        /// </summary>
        public void ViewportToContent(out float x, out float y)
        {
            Vector3 localPosition = content.InverseTransformPoint(viewport.position);
            x = localPosition.x;
            y = localPosition.y;
        }

        /// <summary>
        /// 平滑滚动使目标世界点位于视口中心
        /// </summary>
        public void ScrollToCenter(Transform transform)
        {
            Vector3 world = transform.position;
            ScrollToCenterByWorldXYZ(world.x, world.y, world.z, true);
        }

        /// <summary>
        /// 滚动使世界点 (x,y,z) 位于视口中心（依赖 viewport pivot = (0,1)，viewportCenter 取 (W/2, -H/2)）。
        /// 目标先经 AdjustAnchoredPosition 钳制到内容边界内的可达位置（边缘目标停在最近可达处，中心到不了）；
        /// withAnimate=false 或已在钳制终点 0.5px 内：直接写入终点（含亚像素吸附）并触发 OnScrollToCenter；
        /// withAnimate=true：按 ScrollAnimateDuration 平滑滚动（动画期间位置每帧变，LateUpdate 每帧自动触发）
        /// </summary>
        public void ScrollToCenterByWorldXYZ(float x, float y, float z, bool withAnimate = false)
        {
            StopAllMovement();
            Vector3 localPos = viewport.InverseTransformPoint(new Vector3(x, y, z));
            Vector2 localPosition = localPos;
            Rect rect = viewport.rect;
            Vector2 viewportCenter = new Vector2(rect.width / 2, -rect.height / 2);
            Vector2 diff = viewportCenter - localPosition;

            Vector2 contentAnchored = content.anchoredPosition;
            Vector2 endContentAnchored = contentAnchored + diff;
            float scale = content.localScale.x;
            endContentAnchored = AdjustAnchoredPosition(scale, endContentAnchored / SafeScaleX);

            // 距离判断必须用"钳制后的终点"（endContentAnchored），不能用原始目标点（viewportCenter vs localPosition）：
            // 目标在内容边缘时视口中心永远到不了（被边界钳制停在最近可达处），
            // 原始目标比较恒不成立，边缘格的动画跳转每次都会重新建 tween
            if (!withAnimate || Vector2.Distance(contentAnchored, endContentAnchored) < 0.5f)
            {
                content.anchoredPosition = endContentAnchored;
                _anchorPositionWithoutScale = content.anchoredPosition / SafeScaleX;
                if (null != OnScrollToCenter)
                    OnScrollToCenter();
            }
            else
            {
                Tween move = content.DOAnchorPos(endContentAnchored, ScrollAnimateDuration)
                    .SetEase(Ease.Linear)
                    .OnComplete(() =>
                    {
                        _anchorPositionWithoutScale = content.anchoredPosition / SafeScaleX;
                        if (null != OnScrollToCenter)
                            OnScrollToCenter();
                    });
                _moveCenterTween = move;
            }
        }

        #endregion

        #region 内部工具

        /// <summary>
        /// 刹停一切运动：惯性 + 进行中的跳转动画。
        /// 供外部在"目标已是当前位置、无需滚动"的早退路径调用——
        /// 早退不经过 ScrollToCenterByWorldXYZ 开头的刹车，若不在此处刹住，
        /// 松手后的惯性漂移/未完动画会继续把内容滑走，可能滑出当前格
        /// </summary>
        public void StopAllMovement()
        {
            StopMovement();
            KillMoveTween();
        }

        private void KillMoveTween()
        {
            if (null != _moveCenterTween)
            {
                _moveCenterTween.Kill();
                _moveCenterTween = null;
            }
        }




        #endregion
    }
}
