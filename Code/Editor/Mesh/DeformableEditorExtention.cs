using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace DeformEditor
{
	public partial class DeformableEditor
	{

		private void OnDestroy()
		{
			deformerList?.Dispose();
			deformerList = null;
		}
	}
}
