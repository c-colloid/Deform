using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using Deform;

namespace DeformEditor
{
	public partial class DeformableEditor
	{

		private void OnDestroy()
		{
			deformerList?.Dispose();
			deformerList = null;
		}
		
		public override VisualElement CreateInspectorGUI() {
			var root = base.CreateInspectorGUI() ?? new VisualElement();

			root.Add (new IMGUIContainer(DrawIMGUI(DrawMainSettings)){style = {
				marginBottom = 10
			}});
			
			//root.Add (new IMGUIContainer(DrawIMGUI(DrawDeformersList)){style = {
			//	marginBottom = 10
			//}});
			new ReorderableComponentElementList<Deformer>(root, targets, serializedObject, serializedObject.FindProperty("deformerElements"));

			root.Add (new IMGUIContainer(DrawIMGUI(DrawUtilityToolbar)){style = {
				marginBottom = 10
			}});
			
			root.Add (new IMGUIContainer(DrawIMGUI(DrawDebugInfo)));
			root.Add (new IMGUIContainer(DrawIMGUI(DrawHelpBoxes)));
			
			return root;
		}
		
		Action DrawIMGUI(Action onGUIHandler){
			return () => {
			serializedObject.UpdateIfRequiredOrScript();
			onGUIHandler();
			if (serializedObject.ApplyModifiedProperties())
				EditorApplication.QueuePlayerLoopUpdate();
			};
		}
	}
}