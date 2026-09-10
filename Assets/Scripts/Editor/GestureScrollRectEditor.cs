/*
 * 文件名: GestureScrollRectEditor.cs
 * 作者: zengxin
 * 创建日期: 2026-7-26
 */
using UnityEngine;
using UnityEditor;
using UnityEditor.UI;
using Scx;

namespace Scx.Editor
{
    [CustomEditor(typeof(GestureScrollRect), true)]
    [CanEditMultipleObjects]
    public class GestureScrollRectEditor : ScrollRectEditor
    {
        private SerializedProperty _minScale;
        private SerializedProperty _maxScale;
        private SerializedProperty _scrollWheelStep;
        private SerializedProperty _scaleRatio;
        private SerializedProperty _pinchThreshold;
        private SerializedProperty _canDrag;
        private SerializedProperty _clickEnabled;
        private SerializedProperty _camera;
        private SerializedProperty _scrollAnimateDuration;

        protected override void OnEnable()
        {
            base.OnEnable();
            _minScale = serializedObject.FindProperty("MinScale");
            _maxScale = serializedObject.FindProperty("MaxScale");
            _scrollWheelStep = serializedObject.FindProperty("ScrollWheelStep");
            _scaleRatio = serializedObject.FindProperty("ScaleRatio");
            _pinchThreshold = serializedObject.FindProperty("PinchThreshold");
            _canDrag = serializedObject.FindProperty("CanDrag");
            _clickEnabled = serializedObject.FindProperty("ClickEnabled");
            _camera = serializedObject.FindProperty("Camera");
            _scrollAnimateDuration = serializedObject.FindProperty("ScrollAnimateDuration");
        }

        public override void OnInspectorGUI()
        {
            // 画 ScrollRect 基类字段（保留内置条件渲染：
            // Elasticity 仅 MovementType=Elastic 显示；Scrollbar Visibility/Spacing 仅 Scrollbar 引用非空显示）
            base.OnInspectorGUI();

            // 画 GestureScrollRect 自定义字段
            serializedObject.Update();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("手势缩放 / 拖动配置", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_minScale);
            EditorGUILayout.PropertyField(_maxScale);
            EditorGUILayout.PropertyField(_scrollWheelStep);
            EditorGUILayout.PropertyField(_scaleRatio);
            EditorGUILayout.PropertyField(_pinchThreshold);
            EditorGUILayout.PropertyField(_canDrag);
            EditorGUILayout.PropertyField(_clickEnabled);
            EditorGUILayout.PropertyField(_camera);
            EditorGUILayout.PropertyField(_scrollAnimateDuration);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
