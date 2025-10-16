using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using Deform;
using DeformEditor.Manipulator;

namespace DeformEditor
{
	/// <summary>
	/// Draws a reorderable list of IComponentElements.
	/// </summary>
	/// <typeparam name="T">The type of component the element holds.</typeparam>
	public partial class ReorderableComponentElementList<T> : IDisposable where T : Component
	{
		private ListView listView;
		private Foldout inspectorElement;
		private UnityEngine.Object[] targets;
		private SerializedObject serializedObject;
		private SerializedProperty elements;
		private VisualElement root;
		[SerializeField]
		StyleSheet style;
		[SerializeField]
		VisualTreeAsset listviewelement;
		
		//private bool isSceneGUIRegistered = false;
		// インスタンスを追跡するための静的ディクショナリ
		private static readonly Dictionary<int, WeakReference<ReorderableComponentElementList<T>>> ActiveInstances 
			= new Dictionary<int, WeakReference<ReorderableComponentElementList<T>>>();
		// 静的フィールドでデリゲートと登録状態を管理
		private static Action<SceneView> sceneGUIDelegate;
		private int instanceId;
		private bool isDisposed = false;

		public ReorderableComponentElementList(VisualElement root, UnityEngine.Object[] targets, SerializedObject serializedObject, SerializedProperty elements)
		{
			this.targets = targets;
			this.serializedObject = serializedObject;
			this.elements = elements;
			this.instanceId = GetHashCode();
			
			// インスタンスを追跡用ディクショナリに登録
			lock (ActiveInstances)
			{
				ActiveInstances[instanceId] = new WeakReference<ReorderableComponentElementList<T>>(this);
				CleanupStaleInstances(); // 無効なインスタンスを削除
			}
			
			//Debug.Log($"Constructor called. Instance: {instanceId}, Active instances: {ActiveInstances.Count}");
			style = DeformEditorResources.LoadAssetOfType<StyleSheet>("listview");
			listviewelement = DeformEditorResources.LoadAssetOfType<VisualTreeAsset>("listview");
			InitializeListView(root);
			this.root = root;
		}

		private void InitializeListView(VisualElement root)
		{
			var listviewcontainer = listviewelement.CloneTree();
			listviewcontainer.Q<Label>().text = $"{typeof (T).Name}s";
			listView = 
				listviewcontainer.Q<ListView>();
			listView.name = $"{typeof (T).Name}Elements";
			listView.BindProperty(elements);
			listView.styleSheets.Add(style);
			listView.makeItem = () => new PropertyField(){style = {marginLeft = -10}};
			listView.bindItem = (element, index) =>
			{
				var field = (PropertyField)element;
				field.BindProperty(elements.GetArrayElementAtIndex(index));
			};
			listviewcontainer.style.marginBottom = 1;
			
			listView.selectionChanged += OnSelectionChange;
			listView.itemsChosen += OnSelectionChange;
            
			if (root == null) return;
			
			root.Add(listviewcontainer);
			
			if (inspectorElement == null)
			{
				inspectorElement = new Foldout(){name = "ComponentViewer", style = {
					backgroundImage = DeformEditorResources.GetStyle ("Box").normal.background,
					borderLeftWidth = DeformEditorResources.GetStyle ("Box").border.left,
					borderRightWidth = DeformEditorResources.GetStyle ("Box").border.right,
					borderTopWidth = DeformEditorResources.GetStyle ("Box").border.top,
					borderBottomWidth = DeformEditorResources.GetStyle ("Box").border.bottom,
					marginLeft = DeformEditorResources.GetStyle ("Box").margin.left,
					marginRight = DeformEditorResources.GetStyle ("Box").margin.right,
					marginTop = DeformEditorResources.GetStyle ("Box").margin.top,
					marginBottom = DeformEditorResources.GetStyle ("Box").margin.bottom,
					paddingLeft = DeformEditorResources.GetStyle ("Box").padding.left,
					paddingRight = DeformEditorResources.GetStyle ("Box").padding.right,
					paddingTop = DeformEditorResources.GetStyle ("Box").padding.top,
					paddingBottom =DeformEditorResources.GetStyle ("Box").padding.bottom,
					unitySliceLeft = 1,
					unitySliceRight = 1,
					unitySliceTop = 1,
					unitySliceBottom = 1
				}};
				inspectorElement.style.marginBottom = 4;
				inspectorElement.Q<Toggle>().style.paddingLeft = 24;
			}
			
			if (inspectorElement.parent == null)
			{
				root.Add(inspectorElement);
			}
			
			var box = new Box(){ style = {
				marginBottom = 10,
				paddingTop = 5,
				paddingBottom = 5
			}};
			box.Add(new Label($"+ Drag {typeof(T).Name}s Here"){ style = {
				unityTextAlign = TextAnchor.MiddleCenter,
				color = Color.gray,
				fontSize = 10
			}});
			
			box.AddManipulator(new ComponentDropManipulator<Deformer>(listView,
			(Deformers) =>
			{
				Deformers.ForEach(component =>
				{
					if (elements == null) return;
					
					// SerializedPropertyの配列サイズを増やす
					int newIndex = elements.arraySize;
					elements.InsertArrayElementAtIndex(newIndex);
					//ListViewのcomponentプロパティに追加
					var latestElement = elements.GetArrayElementAtIndex(newIndex);
					latestElement.boxedValue = new DeformerElement(component,true);
					
					elements.serializedObject.ApplyModifiedProperties();
				});
				
				listView.RefreshItems();
			}));
			
			root.Add(box);
		}

		private void OnSelectionChange(IEnumerable<object> selectedItems)
		{
			// インスタンスIDのチェックを追加
			if (instanceId == 0)
			{
				Debug.LogError("Invalid instance ID detected");
				return;
			}
			
			if (isDisposed)
			{
				Debug.LogWarning($"OnSelectionChange called on disposed instance: {instanceId}");
				//return;
			}
			//Debug.Log($"OnSelectionChange called. Instance: {instanceId}");
			
			// 既存のデリゲートを解除
			UnregisterSceneGUI();
			
			inspectorElement.Clear();
			if (selectedItems == null)
			{
				UnregisterSceneGUI();
				return;
			}

			foreach (var item in selectedItems)
			{
				if (item is SerializedProperty property)
				{
					var componentProperty = property.FindPropertyRelative("component");
					var component = componentProperty.objectReferenceValue as Component;

					inspectorElement.style.display = component == null ? DisplayStyle.None : DisplayStyle.Flex;
					if (component != null)
					{
						Editor.CreateCachedEditor(component, null, ref selectedComponentInspectorEditor);
						
						// デリゲートを一度だけ登録
						RegisterSceneGUI();
						
						var foldoutName = $"{ObjectNames.NicifyVariableName (componentProperty.objectReferenceValue.GetType ().Name)} Properties";
						inspectorElement.text = foldoutName;
						inspectorElement.Add(selectedComponentInspectorEditor?.CreateInspectorGUI() ?? new IMGUIContainer(() => selectedComponentInspectorEditor?.OnInspectorGUI()));
					}
				}
			}
		}
		
		private void RegisterSceneGUI()
		{
			if (isDisposed)
			{
				Debug.LogWarning($"Attempting to register SceneGUI on disposed instance: {instanceId}");
				return;
			}

			if (sceneGUIDelegate == null)
			{
				sceneGUIDelegate = new Action<SceneView>(SceneGUI);
				SceneView.duringSceneGui += sceneGUIDelegate;
				//Debug.Log($"SceneGUI registered. Instance: {instanceId}, Active instances: {ActiveInstances.Count}");
			}
		}

		private void UnregisterSceneGUI()
		{
			if (sceneGUIDelegate != null)
			{
				SceneView.duringSceneGui -= sceneGUIDelegate;
				//Debug.Log($"SceneGUI unregistered. Instance: {instanceId}, Active instances: {ActiveInstances.Count}");
				sceneGUIDelegate = null;
			}
		}
		
		// デバッグ用のデストラクタ
		~ReorderableComponentElementList()
		{
			if (!isDisposed)
			{
				Debug.LogWarning($"Instance {instanceId} was not properly disposed");
			}
		}
		
		private static void CleanupStaleInstances()
		{
			var staleKeys = new List<int>();
			foreach (var kvp in ActiveInstances)
			{
				if (!kvp.Value.TryGetTarget(out _))
				{
					staleKeys.Add(kvp.Key);
				}
			}

			foreach (var key in staleKeys)
			{
				ActiveInstances.Remove(key);
				Debug.Log($"Removed stale instance: {key}");
			}
		}
		
		// 既存のインスタンスをすべて破棄するスタティックメソッド
		public static void DisposeAllInstances()
		{
			lock (ActiveInstances)
			{
				ActiveInstances.Values
					.Where(weakRef => weakRef.TryGetTarget(out var instance))
					.Select(weakRef => { weakRef.TryGetTarget(out var instance); return instance; })
					.ToList()  // ToListで即時実行して、Dispose中のコレクション変更を防ぐ
					.ForEach(instance => instance.Dispose());

				ActiveInstances.Clear();
			}
		}
	}
}