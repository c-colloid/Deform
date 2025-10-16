using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using Deform;

namespace DeformEditor.Manipulator
{
	public class ComponentDropManipulator<T> : PointerManipulator where T : Component
	{
		private ListView dstList;
		private ObjectField dstField;
		private IEnumerable<T> refarences;
		private Action<List<T>> onDragPerform;
		
		public ComponentDropManipulator(VisualElement assignTarget, Action<List<T>> onDragPerformCallback)
		{
			if (assignTarget is ListView)
			{
				dstList = assignTarget as ListView;
			}
			else if (assignTarget is ObjectField)
			{
				dstField = assignTarget as ObjectField;
			}
			onDragPerform = onDragPerformCallback;
		}
		
		protected override void RegisterCallbacksOnTarget()
		{
			target.RegisterCallback<DragEnterEvent>(DragEnter);
			target.RegisterCallback<DragPerformEvent>(DragPerform);
			target.RegisterCallback<DragUpdatedEvent>(DragUpdated);
		}
		
		protected override void UnregisterCallbacksFromTarget()
		{
			target.UnregisterCallback<DragEnterEvent>(DragEnter);
			target.UnregisterCallback<DragPerformEvent>(DragPerform);
			target.UnregisterCallback<DragUpdatedEvent>(DragUpdated);
		}
		
		private void DragEnter(DragEnterEvent evt)
		{
			refarences = DragAndDrop.objectReferences
				.Select(o => o switch
				{
					GameObject go => go.GetComponent<T>(),
					T component => component,
					_ => null
				})
				.Where(c => c != null);
		}
		
		private void DragPerform(DragPerformEvent evt)
		{
			if (dstList == null && dstField == null) return;
			if (refarences.Count() <= 0) return;
			
			onDragPerform?.Invoke(refarences.ToList());
		}
		
		private void DragUpdated(DragUpdatedEvent evt)
		{
			if (refarences == null) return;
			if (refarences.Count() <= 0) return;
			
			DragAndDrop.visualMode = DragAndDropVisualMode.Link;
		}
	}
}