using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Deform;
using Unity.Mathematics;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using float3 = Unity.Mathematics.float3;
using MirrorAxis = Deform.LatticeDeformer.MirrorAxis;
using Unity.Collections;

namespace DeformEditor
{
    [CustomEditor(typeof(LatticeDeformer))]
	public class LatticeDeformerEditor : DeformerEditor
    {
        private Vector3Int newResolution;

        private float3 handleScale = Vector3.one;
        private Tool activeTool = Tool.None;

        enum MouseDragState
        {
            NotActive,
            Eligible,
            InProgress
        }

        private MouseDragState mouseDragState = MouseDragState.NotActive;
        private Vector2 mouseDownPosition;
        private int previousSelectionCount = 0;

        // Positions of selected points before a rotate or scale begins
        private List<float3> selectedOriginalPositions = new List<float3>();
        
        // Positions and resolution before a resize
        private float3[] cachedResizePositions = new float3[0];
        private Vector3Int cachedResizeResolution;
        
        [SerializeField] private List<int> selectedIndices = new List<int>();

        private static class Content
        {
            public static readonly GUIContent Resolution = new GUIContent(text: "Resolution", tooltip: "Per axis control point counts, the higher the resolution the more splits");
            public static readonly GUIContent Mode = new GUIContent(text: "Mode", tooltip: "Mode by which vertices are positioned between control points");
	        public static readonly GUIContent StopEditing = new GUIContent(text: "Stop Editing Control Points", tooltip: "Restore normal transform tools\n\nShortcut: Escape");
            
	        public static readonly GUIContent MirrorAxis = new GUIContent(text: "MirrorAxis", tooltip: "Selecting the axis of symmetry for mirror editing");
	        public static readonly GUIContent MirrorCenter = new GUIContent(text: "MirrorCenter", tooltip: "Central Potition of Mirror Editing");
        }

        private class Properties
        {
            public SerializedProperty Resolution;
	        public SerializedProperty Mode;
	        
	        public SerializedProperty MirrorAxis;
	        public SerializedProperty MirrorCenter;

            public Properties(SerializedObject obj)
            {
                Resolution = obj.FindProperty("resolution");
	            Mode = obj.FindProperty("mode");
	            
	            MirrorAxis = obj.FindProperty("mirrorAxis");
	            MirrorCenter = obj.FindProperty("mirrorCenter");
            }
        }

        private Properties properties;

        protected override void OnEnable()
        {
            base.OnEnable();

            properties = new Properties(serializedObject);
            
            LatticeDeformer latticeDeformer = ((LatticeDeformer) target);
            newResolution = latticeDeformer.Resolution;
            CacheResizePositionsFromChange();
            
            Undo.undoRedoPerformed += UndoRedoPerformed;
        }

        private void UndoRedoPerformed()
        {
            LatticeDeformer latticeDeformer = ((LatticeDeformer) target);
            newResolution = latticeDeformer.Resolution;
            CacheResizePositionsFromChange();
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            LatticeDeformer latticeDeformer = ((LatticeDeformer) target);

            serializedObject.UpdateIfRequiredOrScript();

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.PropertyField(properties.Mode);
            newResolution = EditorGUILayout.Vector3IntField(Content.Resolution, newResolution);
            // Make sure we have at least two control points per axis
            newResolution = Vector3Int.Max(newResolution, new Vector3Int(2, 2, 2));
            // Don't let the lattice resolution get ridiculously high
	        newResolution = Vector3Int.Min(newResolution, new Vector3Int(32, 32, 32));
	        
	        EditorGUILayout.PropertyField(properties.MirrorAxis);

	        EditorGUILayout.PropertyField(properties.MirrorCenter);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(target, "Update Lattice");
                latticeDeformer.GenerateControlPoints(newResolution, cachedResizePositions, cachedResizeResolution);
                selectedIndices.Clear();
            }

            if (GUILayout.Button("Reset Lattice Points"))
            {
                Undo.RecordObject(target, "Reset Lattice Points");
                latticeDeformer.GenerateControlPoints(newResolution);
                selectedIndices.Clear();
                
                CacheResizePositionsFromChange();
            }

            if (latticeDeformer.CanAutoFitBounds)
            {
                if (GUILayout.Button("Auto-Fit Bounds"))
                {
                    Undo.RecordObject(latticeDeformer.transform, "Auto-Fit Bounds");
                    latticeDeformer.FitBoundsToParentDeformable();
                }
            }

            serializedObject.ApplyModifiedProperties();

            EditorApplication.QueuePlayerLoopUpdate();
        }

        public override void OnSceneGUI()
	    {
		    if (target == null) return;
            base.OnSceneGUI();

            LatticeDeformer lattice = target as LatticeDeformer;
            Transform transform = lattice.transform;
            float3[] controlPoints = lattice.ControlPoints;
	        Event e = Event.current;

            using (new Handles.DrawingScope(transform.localToWorldMatrix))
            {
                var cachedZTest = Handles.zTest;

                // Change the depth testing to only show handles in front of solid objects (i.e. typical depth testing) 
                Handles.zTest = CompareFunction.LessEqual;
                DrawLattice(lattice, DeformHandles.LineMode.Solid);
                // Change the depth testing to only show handles *behind* solid objects 
                Handles.zTest = CompareFunction.Greater;
                DrawLattice(lattice, DeformHandles.LineMode.Light);

                // Restore the original z test value now we're done with our drawing
                Handles.zTest = cachedZTest;

                var resolution = lattice.Resolution;
                for (int z = 0; z < resolution.z; z++)
                {
                    for (int y = 0; y < resolution.y; y++)
                    {
                        for (int x = 0; x < resolution.x; x++)
                        {
                            var controlPointHandleID = GUIUtility.GetControlID("LatticeDeformerControlPoint".GetHashCode(), FocusType.Passive);
                            var activeColor = DeformEditorSettings.SolidHandleColor;
                            var controlPointIndex = lattice.GetIndex(x, y, z);

                            if (GUIUtility.hotControl == controlPointHandleID || selectedIndices.Contains(controlPointIndex))
                            {
                                activeColor = Handles.selectedColor;
                            }
                            else if (HandleUtility.nearestControl == controlPointHandleID)
                            {
                                activeColor = Handles.preselectionColor;
                            }

                            if (e.type == EventType.MouseDown && HandleUtility.nearestControl == controlPointHandleID && e.button == 0 && MouseActionAllowed)
                            {
                                BeginSelectionChangeRegion();
                                GUIUtility.hotControl = controlPointHandleID;
                                GUIUtility.keyboardControl = controlPointHandleID;
                                e.Use();

                                bool modifierKeyPressed = e.control || e.shift || e.command;

                                if (modifierKeyPressed && selectedIndices.Contains(controlPointIndex))
                                {
                                    // Pressed a modifier key so toggle the selection
                                    selectedIndices.Remove(controlPointIndex);
                                }
                                else
                                {
                                    if (!modifierKeyPressed)
                                    {
                                        selectedIndices.Clear();
                                    }

                                    if (!selectedIndices.Contains(controlPointIndex))
                                    {
                                        selectedIndices.Add(controlPointIndex);
                                    }
                                }

                                EndSelectionChangeRegion();
                            }

                            if (Tools.current != Tool.None && selectedIndices.Count != 0)
                            {
                                // If the user changes tool, change our internal mode to match but disable the corresponding Unity tool
                                // (e.g. they hit W key or press on the Rotate Tool button on the top left toolbar) 
                                activeTool = Tools.current;
                                Tools.current = Tool.None;
                            }

                            using (new Handles.DrawingScope(activeColor))
                            {
                                var position = controlPoints[controlPointIndex];
                                var size = HandleUtility.GetHandleSize(position) * DeformEditorSettings.ScreenspaceLatticeHandleCapSize;

                                Handles.DotHandleCap(
                                    controlPointHandleID,
                                    position,
                                    Quaternion.identity,
                                    size,
                                    e.type);
                            }
                        }
                    }
                }
            }

            var defaultControl = DeformUnityObjectSelection.DisableSceneViewObjectSelection();

            if (selectedIndices.Count != 0)
            {
	            var currentPivotPosition = float3.zero;
                
	            LatticeDeformer.MirrorAxis mirrorAxisEnum = (LatticeDeformer.MirrorAxis)properties.MirrorAxis.intValue;
	            Transform mirrorCenterTransform = properties.MirrorCenter.objectReferenceValue as Transform;

                if (Tools.pivotMode == PivotMode.Center)
                {
                    // Get the average position
                    foreach (var index in selectedIndices)
                    {
                        currentPivotPosition += controlPoints[index];
                    }

                    currentPivotPosition /= selectedIndices.Count;
                }
                else
                {
                    // Match the scene view behaviour that Pivot mode uses the last selected object as pivot
                    currentPivotPosition = controlPoints[selectedIndices.Last()];
                }

                float3 handlePosition = transform.TransformPoint(currentPivotPosition);

                if (e.type == EventType.MouseDown)
                {
                    // Potentially started interacting with a handle so reset everything
                    handleScale = Vector3.one;
                    // Make sure we cache the positions just before the interaction changes them
                    CacheOriginalPositions();
                }

                var originalPivotPosition = float3.zero;

                if (Tools.pivotMode == PivotMode.Center)
                {
                    // Get the average position
                    foreach (var originalPosition in selectedOriginalPositions)
                    {
                        originalPivotPosition += originalPosition;
                    }

                    originalPivotPosition /= selectedIndices.Count;
                }
                else
                {
                    // Match the scene view behaviour that Pivot mode uses the last selected object as pivot
                    originalPivotPosition = selectedOriginalPositions.LastOrDefault();
                }

                var handleRotation = transform.rotation;
                if (Tools.pivotRotation == PivotRotation.Global)
                {
                    handleRotation = Quaternion.identity;
                }

	            var resolution = lattice.Resolution;
	            
                if (activeTool == Tool.Move)
                {
                    EditorGUI.BeginChangeCheck();
                    float3 newPosition = Handles.PositionHandle(handlePosition, handleRotation);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Update Lattice");

                        var delta = newPosition - handlePosition;
	                    delta = transform.InverseTransformVector(delta);
	                    
                        //foreach (var selectedIndex in selectedIndices)
                        //{
	                    //    controlPoints[selectedIndex] += delta;
                        //}
                            
	                    // ミラー軸の設定を取得
	                    Vector3Int mirrorAxis = new Vector3Int(
		                    mirrorAxisEnum.HasFlag(MirrorAxis.X) ? 1 : 0,
		                    mirrorAxisEnum.HasFlag(MirrorAxis.Y) ? 1 : 0,
		                    mirrorAxisEnum.HasFlag(MirrorAxis.Z) ? 1 : 0
	                    );

	                    Vector3 mirrorCenter = mirrorCenterTransform != null ? 
		                    transform.InverseTransformPoint(mirrorCenterTransform.position) : 
		                    Vector3.zero;
	                    
	                    // 選択中の点のうち、代表点とその従属点の関係を特定
	                    var leaderPoints = new HashSet<int>();
	                    var followerMapping = new Dictionary<int, int>(); // follower -> leader

	                    foreach (var index in selectedIndices)
	                    {
		                    if (followerMapping.ContainsKey(index))
			                    continue;

		                    bool isLeader = true;
		                    foreach (var otherIndex in selectedIndices)
		                    {
			                    if (index == otherIndex) continue;

			                    var mirroredIndices = GetAllMirroredIndices(otherIndex, resolution, mirrorCenter, mirrorAxis);
			                    if (mirroredIndices.Contains(index))
			                    {
				                    // 既に処理済みの点のミラーである場合
				                    if (leaderPoints.Contains(otherIndex))
				                    {
					                    isLeader = false;
					                    followerMapping[index] = otherIndex;
					                    break;
				                    }
			                    }
		                    }

		                    if (isLeader)
		                    {
			                    leaderPoints.Add(index);
		                    }
	                    }

	                    // 代表点の移動を適用
	                    foreach (var leaderIndex in leaderPoints)
	                    {
		                    controlPoints[leaderIndex] += delta;

		                    // この代表点に従属する選択された点の移動
		                    foreach (var pair in followerMapping)
		                    {
			                    if (pair.Value == leaderIndex)
			                    {
				                    float3 mirroredDelta = CalculateMirroredDelta(leaderIndex, pair.Key, delta, resolution, mirrorAxis);
				                    controlPoints[pair.Key] += mirroredDelta;
			                    }
		                    }

		                    // 非選択のミラー点の移動
		                    var mirroredIndices = GetAllMirroredIndices(leaderIndex, resolution, mirrorCenter, mirrorAxis);
		                    foreach (var mirroredIndex in mirroredIndices)
		                    {
			                    if (!selectedIndices.Contains(mirroredIndex))
			                    {
				                    float3 mirroredDelta = CalculateMirroredDelta(leaderIndex, mirroredIndex, delta, resolution, mirrorAxis);
				                    controlPoints[mirroredIndex] += mirroredDelta;
			                    }
		                    }
	                    }
                        
                        CacheResizePositionsFromChange();
                    }
                }
                else if (activeTool == Tool.Rotate)
                {
                    EditorGUI.BeginChangeCheck();
                    quaternion newRotation = Handles.RotationHandle(handleRotation, handlePosition);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Update Lattice");

                        for (var index = 0; index < selectedIndices.Count; index++)
                        {
                        	//ミラー編集用のインデックス
                        	int selectedIndex = selectedIndices[index];
	                        int mirroredIndex = GetMirroredIndex(selectedIndex, resolution, properties.MirrorCenter.vector3Value, properties.MirrorAxis.vector3IntValue);

                            if (Tools.pivotRotation == PivotRotation.Global)
                            {
	                            controlPoints[selectedIndices[index]] = originalPivotPosition + (float3) transform.InverseTransformDirection(mul(newRotation, transform.TransformDirection(selectedOriginalPositions[index] - originalPivotPosition)));
                                
	                            //ミラー編集
	                            if (mirroredIndex != -1 && mirroredIndex != selectedIndex)
	                            {
		                            controlPoints[mirroredIndex] = originalPivotPosition + (float3)transform.InverseTransformDirection(
			                            mul(newRotation, transform.TransformDirection(selectedOriginalPositions[index] - originalPivotPosition))
		                            );
	                            }
                            }
                            else
                            {
	                            controlPoints[selectedIndices[index]] = originalPivotPosition + mul(mul(inverse(handleRotation), newRotation), (selectedOriginalPositions[index] - originalPivotPosition));
                                
	                            //ミラー編集
	                            if (mirroredIndex != -1 && mirroredIndex != selectedIndex)
	                            {
		                            controlPoints[mirroredIndex] = originalPivotPosition + mul(mul(inverse(handleRotation), newRotation), (selectedOriginalPositions[index] - originalPivotPosition));
	                            }
                            }
                        }
                        
                        CacheResizePositionsFromChange();
                    }
                }
                else if (activeTool == Tool.Scale)
                {
                    var size = HandleUtility.GetHandleSize(handlePosition);
                    EditorGUI.BeginChangeCheck();
                    handleScale = Handles.ScaleHandle(handleScale, handlePosition, handleRotation, size);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Update Lattice");

                        for (var index = 0; index < selectedIndices.Count; index++)
                        {
                            if (Tools.pivotRotation == PivotRotation.Global)
                            {
                                controlPoints[selectedIndices[index]] = originalPivotPosition + (float3) transform.InverseTransformDirection(handleScale * transform.TransformDirection(selectedOriginalPositions[index] - originalPivotPosition));
                            }
                            else
                            {
                                controlPoints[selectedIndices[index]] = originalPivotPosition + handleScale * (selectedOriginalPositions[index] - originalPivotPosition);
                            }
                        }
                        
                        CacheResizePositionsFromChange();
                    }
                }

                Handles.BeginGUI();
                if (GUI.Button(new Rect((EditorGUIUtility.currentViewWidth - 200) / 2, SceneView.currentDrawingSceneView.position.height - 60, 200, 30), Content.StopEditing))
                {
                    DeselectAll();
                }

                Handles.EndGUI();
            }

            if (e.button == 0) // Left Mouse Button
            {
                if (e.type == EventType.MouseDown && HandleUtility.nearestControl == defaultControl && MouseActionAllowed)
                {
                    mouseDownPosition = e.mousePosition;
                    mouseDragState = MouseDragState.Eligible;
                }
                else if (e.type == EventType.MouseDrag && mouseDragState == MouseDragState.Eligible)
                {
                    mouseDragState = MouseDragState.InProgress;
                    SceneView.currentDrawingSceneView.Repaint();
                }
                else if (GUIUtility.hotControl == 0 &&
                         (e.type == EventType.MouseUp
                          || (mouseDragState == MouseDragState.InProgress && e.rawType == EventType.MouseUp))) // Have they released the mouse outside the scene view while doing marquee select?
                {
                    if (mouseDragState == MouseDragState.InProgress)
                    {
                        var mouseUpPosition = e.mousePosition;

                        Rect marqueeRect = Rect.MinMaxRect(Mathf.Min(mouseDownPosition.x, mouseUpPosition.x),
                            Mathf.Min(mouseDownPosition.y, mouseUpPosition.y),
                            Mathf.Max(mouseDownPosition.x, mouseUpPosition.x),
                            Mathf.Max(mouseDownPosition.y, mouseUpPosition.y));

                        BeginSelectionChangeRegion();

                        if (!e.shift && !e.control && !e.command)
                        {
                            selectedIndices.Clear();
                        }

                        for (var index = 0; index < controlPoints.Length; index++)
                        {
                            Camera camera = SceneView.currentDrawingSceneView.camera;
                            var screenPoint = DeformEditorGUIUtility.WorldToGUIPoint(camera, transform.TransformPoint(controlPoints[index]));

                            if (screenPoint.z < 0)
                            {
                                // Don't consider points that are behind the camera
                                continue;
                            }

                            if (marqueeRect.Contains(screenPoint))
                            {
                                if (e.control || e.command) // Remove selection
                                {
                                    selectedIndices.Remove(index);
                                }
                                else
                                {
                                    selectedIndices.Add(index);
                                }
                            }
                        }

                        EndSelectionChangeRegion();
                    }
                    else
                    {
	                    
	                    if (selectedIndices.Count == 0)
	                    {
		                    try
		                    {
			                    // シーンビューでの選択処理をtry-catchで囲む
			                    if (e.type == EventType.MouseUp && e.button == 0)
			                    {
				                    DeformUnityObjectSelection.AttemptMouseUpObjectSelection();
			                    }
		                    }
			                    catch (System.NullReferenceException)
			                    {
				                    // null参照例外を安全に処理
			                    }
	                    }
	                    else
	                    {
		                    DeselectAll();
	                    }
                    }

	                mouseDragState = MouseDragState.NotActive;
                    
                }
            }

            if (e.type == EventType.Repaint && mouseDragState == MouseDragState.InProgress)
            {
                var mouseUpPosition = e.mousePosition;

                Rect marqueeRect = Rect.MinMaxRect(Mathf.Min(mouseDownPosition.x, mouseUpPosition.x),
                    Mathf.Min(mouseDownPosition.y, mouseUpPosition.y),
                    Mathf.Max(mouseDownPosition.x, mouseUpPosition.x),
                    Mathf.Max(mouseDownPosition.y, mouseUpPosition.y));
                DeformUnityObjectSelection.DrawUnityStyleMarquee(marqueeRect);
                SceneView.RepaintAll();
            }

            // If the lattice is visible, override Unity's built-in Select All so that it selects all control points 
            if (DeformUnityObjectSelection.SelectAllPressed)
            {
                BeginSelectionChangeRegion();
                selectedIndices.Clear();
                var resolution = lattice.Resolution;
                for (int z = 0; z < resolution.z; z++)
                {
                    for (int y = 0; y < resolution.y; y++)
                    {
                        for (int x = 0; x < resolution.x; x++)
                        {
                            var controlPointIndex = lattice.GetIndex(x, y, z);
                            selectedIndices.Add(controlPointIndex);
                        }
                    }
                }

                EndSelectionChangeRegion();

                e.Use();
            }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                DeselectAll();
            }

            EditorApplication.QueuePlayerLoopUpdate();
        }

        private void DeselectAll()
        {
            BeginSelectionChangeRegion();
            selectedIndices.Clear();
            EndSelectionChangeRegion();
        }

        private void BeginSelectionChangeRegion()
        {
            Undo.RecordObject(this, "Selection Change");
            previousSelectionCount = selectedIndices.Count;
        }

        private void EndSelectionChangeRegion()
        {
            if (selectedIndices.Count != previousSelectionCount)
            {
                if (selectedIndices.Count != 0 && previousSelectionCount == 0 && Tools.current == Tool.None) // Is this our first selection?
                {
                    // Make sure when we start selecting control points we actually have a useful tool equipped
                    activeTool = Tool.Move;
                }
                else if (selectedIndices.Count == 0 && previousSelectionCount != 0)
                {
                    // If we have deselected we should probably restore the active tool from before
                    Tools.current = activeTool;
                }
                
                // Selected positions have changed so make sure we're up to date
                CacheOriginalPositions();

                // Different UI elements may be visible depending on selection count, so redraw when it changes
                Repaint();
            }
        }

        private void CacheOriginalPositions()
        {
            // Cache the selected control point positions before the interaction, so that all handle
            // transformations are done using the original values rather than compounding error each frame
            var latticeDeformer = (target as LatticeDeformer);
            float3[] controlPoints = latticeDeformer.ControlPoints;
            selectedOriginalPositions.Clear();
            foreach (int selectedIndex in selectedIndices)
            {
                selectedOriginalPositions.Add(controlPoints[selectedIndex]);
            }
        }

        private void CacheResizePositionsFromChange()
        {
            var latticeDeformer = (target as LatticeDeformer);
            float3[] controlPoints = latticeDeformer.ControlPoints;
            cachedResizePositions = new float3[controlPoints.Length];
            controlPoints.CopyTo(cachedResizePositions, 0);

            cachedResizeResolution = latticeDeformer.Resolution;
        }

        private static bool MouseActionAllowed
        {
            get
            {
                if (Event.current.alt) return false;

                return true;
            }
        }

        private void DrawLattice(LatticeDeformer lattice, DeformHandles.LineMode lineMode)
        {
            var resolution = lattice.Resolution;
	        var controlPoints = lattice.ControlPoints;
            
	        // MirrorAxisの取得を修正
	        LatticeDeformer.MirrorAxis mirrorAxisEnum = (LatticeDeformer.MirrorAxis)properties.MirrorAxis.intValue;
	        Vector3Int mirrorAxis = new Vector3Int(
		        (mirrorAxisEnum.HasFlag(LatticeDeformer.MirrorAxis.X)) ? 1 : 0,
		        (mirrorAxisEnum.HasFlag(LatticeDeformer.MirrorAxis.Y)) ? 1 : 0,
		        (mirrorAxisEnum.HasFlag(LatticeDeformer.MirrorAxis.Z)) ? 1 : 0
	        );
	        
	        // MirrorCenterの取得を修正（Transform型として）
	        Transform mirrorCenterTransform = properties.MirrorCenter.objectReferenceValue as Transform;
	        Vector3 mirrorCenter = mirrorCenterTransform != null ? mirrorCenterTransform.position : Vector3.zero;

	        // Transform座標をローカル空間に変換
	        if (mirrorCenterTransform != null)
	        {
		        mirrorCenter = lattice.transform.InverseTransformPoint(mirrorCenter);
	        }
	        
	        // オリジナルの格子を描画
	        DrawLatticeGrid(lattice, resolution, controlPoints, lineMode);

	        // ミラー側の格子を描画（選択されている軸に応じて）
	        if (mirrorAxis != Vector3Int.zero)
	        {
		        var mirroredPoints = new float3[controlPoints.Length];
		        for (int i = 0; i < controlPoints.Length; i++)
		        {
			        mirroredPoints[i] = controlPoints[i];
			        int mirroredIndex = GetMirroredIndex(i, resolution, mirrorCenter, mirrorAxis);
			        if (mirroredIndex != -1 && mirroredIndex != i)
			        {
				        mirroredPoints[i] = controlPoints[mirroredIndex];
			        }
		        }
		        DrawLatticeGrid(lattice, resolution, mirroredPoints, lineMode);
	        }
        }
        
	    private void DrawLatticeGrid(LatticeDeformer lattice, Vector3Int resolution, float3[] controlPoints, DeformHandles.LineMode lineMode)
	    {
            for (int z = 0; z < resolution.z - 1; z++)
            {
                for (int y = 0; y < resolution.y - 1; y++)
                {
                    for (int x = 0; x < resolution.x - 1; x++)
                    {
                        int index000 = lattice.GetIndex(x, y, z);
                        int index100 = lattice.GetIndex(x + 1, y, z);
                        int index010 = lattice.GetIndex(x, y + 1, z);
                        int index110 = lattice.GetIndex(x + 1, y + 1, z);
                        int index001 = lattice.GetIndex(x, y, z + 1);
                        int index101 = lattice.GetIndex(x + 1, y, z + 1);
                        int index011 = lattice.GetIndex(x, y + 1, z + 1);
                        int index111 = lattice.GetIndex(x + 1, y + 1, z + 1);

                        DeformHandles.Line(controlPoints[index000], controlPoints[index100], lineMode);
                        DeformHandles.Line(controlPoints[index010], controlPoints[index110], lineMode);
                        DeformHandles.Line(controlPoints[index001], controlPoints[index101], lineMode);
                        DeformHandles.Line(controlPoints[index011], controlPoints[index111], lineMode);

                        DeformHandles.Line(controlPoints[index000], controlPoints[index010], lineMode);
                        DeformHandles.Line(controlPoints[index100], controlPoints[index110], lineMode);
                        DeformHandles.Line(controlPoints[index001], controlPoints[index011], lineMode);
                        DeformHandles.Line(controlPoints[index101], controlPoints[index111], lineMode);

                        DeformHandles.Line(controlPoints[index000], controlPoints[index001], lineMode);
                        DeformHandles.Line(controlPoints[index100], controlPoints[index101], lineMode);
                        DeformHandles.Line(controlPoints[index010], controlPoints[index011], lineMode);
                        DeformHandles.Line(controlPoints[index110], controlPoints[index111], lineMode);
                    }
                }
            }
        }
        
	    private int GetMirroredIndex(int index, Vector3Int resolution, Vector3 mirrorCenter, Vector3Int mirrorAxis)
	    {
		    // 1D index から 3D 座標に変換
		    int x = index % resolution.x;
		    int y = (index / resolution.x) % resolution.y;
		    int z = index / (resolution.x * resolution.y);

		    // 格子の中心を計算
		    float centerX = (resolution.x - 1) * 0.5f + mirrorCenter.x;
		    float centerY = (resolution.y - 1) * 0.5f + mirrorCenter.y;
		    float centerZ = (resolution.z - 1) * 0.5f + mirrorCenter.z;

		    // ミラー点の計算
		    int mirrorX = mirrorAxis.x != 0 ? Mathf.RoundToInt(2 * centerX - x) : x;
		    int mirrorY = mirrorAxis.y != 0 ? Mathf.RoundToInt(2 * centerY - y) : y;
		    int mirrorZ = mirrorAxis.z != 0 ? Mathf.RoundToInt(2 * centerZ - z) : z;

		    // 範囲チェック
		    if (mirrorX < 0 || mirrorX >= resolution.x || 
			    mirrorY < 0 || mirrorY >= resolution.y || 
			    mirrorZ < 0 || mirrorZ >= resolution.z)
		    {
			    return -1;
		    }

		    int mirroredIndex = (mirrorZ * resolution.y + mirrorY) * resolution.x + mirrorX;
		    return mirroredIndex == index ? -1 : mirroredIndex;
	    }
	    
	    private List<int> GetAllMirroredIndices(int index, Vector3Int resolution, Vector3 mirrorCenter, Vector3Int mirrorAxis)
	    {
		    List<int> mirroredIndices = new List<int>();
    
		    // 1D index から 3D 座標に変換
		    int x = index % resolution.x;
		    int y = (index / resolution.x) % resolution.y;
		    int z = index / (resolution.x * resolution.y);

		    // 格子の中心を計算
		    float centerX = (resolution.x - 1) * 0.5f + mirrorCenter.x;
		    float centerY = (resolution.y - 1) * 0.5f + mirrorCenter.y;
		    float centerZ = (resolution.z - 1) * 0.5f + mirrorCenter.z;

		    // 各軸の反転状態を配列で保持 (false: そのまま, true: 反転)
		    bool[] xFlip = mirrorAxis.x != 0 ? new[] { false, true } : new[] { false };
		    bool[] yFlip = mirrorAxis.y != 0 ? new[] { false, true } : new[] { false };
		    bool[] zFlip = mirrorAxis.z != 0 ? new[] { false, true } : new[] { false };

		    // すべての組み合わせを試す
		    foreach (bool flipX in xFlip)
		    {
			    foreach (bool flipY in yFlip)
			    {
				    foreach (bool flipZ in zFlip)
				    {
					    // 元の点と同じ組み合わせはスキップ
					    if (!flipX && !flipY && !flipZ) continue;

					    // ミラー点の計算
					    int mirrorX = flipX ? Mathf.RoundToInt(2 * centerX - x) : x;
					    int mirrorY = flipY ? Mathf.RoundToInt(2 * centerY - y) : y;
					    int mirrorZ = flipZ ? Mathf.RoundToInt(2 * centerZ - z) : z;

					    // 範囲チェック
					    if (mirrorX < 0 || mirrorX >= resolution.x || 
						    mirrorY < 0 || mirrorY >= resolution.y || 
						    mirrorZ < 0 || mirrorZ >= resolution.z)
					    {
						    continue;
					    }

					    int mirroredIndex = (mirrorZ * resolution.y + mirrorY) * resolution.x + mirrorX;
					    if (mirroredIndex != index && !mirroredIndices.Contains(mirroredIndex))
					    {
						    mirroredIndices.Add(mirroredIndex);
					    }
				    }
			    }
		    }

		    return mirroredIndices;
	    }
	    
	    private float3 CalculateMirroredDelta(int sourceIndex, int mirroredIndex, float3 sourceDelta, Vector3Int resolution, Vector3Int mirrorAxis)
	    {
		    // インデックスから3D座標に変換
		    int x1 = sourceIndex % resolution.x;
		    int x2 = mirroredIndex % resolution.x;
		    int y1 = (sourceIndex / resolution.x) % resolution.y;
		    int y2 = (mirroredIndex / resolution.x) % resolution.y;
		    int z1 = sourceIndex / (resolution.x * resolution.y);
		    int z2 = mirroredIndex / (resolution.x * resolution.y);

		    return new float3(
			    mirrorAxis.x != 0 && x1 != x2 ? -sourceDelta.x : sourceDelta.x,
			    mirrorAxis.y != 0 && y1 != y2 ? -sourceDelta.y : sourceDelta.y,
			    mirrorAxis.z != 0 && z1 != z2 ? -sourceDelta.z : sourceDelta.z
		    );
	    }
	    
    }
}