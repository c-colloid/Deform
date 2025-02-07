using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;
using float3 = Unity.Mathematics.float3;
using float4x4 = Unity.Mathematics.float4x4;

namespace Deform
{
	public partial class LatticeDeformer : Deformer
	{
		[System.Flags]
		public enum MirrorAxis
		{
			None = 0,
			X = 1 << 0,
			Y = 1 << 1,
			Z = 1 << 2
		}
		
		public Transform MirrorCenter
		{
			get
			{
				if (mirrorCenter == null)
					mirrorCenter = transform;
				return mirrorCenter;
			}
			set => mirrorCenter = value;
		}
		
		[SerializeField] private MirrorAxis mirrorAxis = MirrorAxis.None;
		[SerializeField] private Transform mirrorCenter; // ワールド空間でのミラー中心点
		
		[BurstCompile(CompileSynchronously = COMPILE_SYNCHRONOUSLY)]
		public struct MirroredLatticeJob : IJobParallelFor
		{
			[DeallocateOnJobCompletion, ReadOnly] public NativeArray<float3> controlPoints;
			[ReadOnly] public int3 resolution;
			[ReadOnly] public float4x4 meshToTarget;
			[ReadOnly] public float4x4 targetToMesh;
			[ReadOnly] public int mirrorAxis;
			[ReadOnly] public float3 mirrorCenter;
			public NativeArray<float3> vertices;

			public void Execute(int index)
			{
				var sourcePosition = transform(meshToTarget, vertices[index]) + float3(0.5f, 0.5f, 0.5f);
				var originalPosition = sourcePosition;
				var isMirrored = false;

				// ミラーリングの適用
				if ((mirrorAxis & 1) != 0 && sourcePosition.x > mirrorCenter.x) // X軸
				{
					sourcePosition.x = mirrorCenter.x - (sourcePosition.x - mirrorCenter.x);
					isMirrored = true;
				}
				if ((mirrorAxis & 2) != 0 && sourcePosition.y > mirrorCenter.y) // Y軸
				{
					sourcePosition.y = mirrorCenter.y - (sourcePosition.y - mirrorCenter.y);
					isMirrored = true;
				}
				if ((mirrorAxis & 4) != 0 && sourcePosition.z > mirrorCenter.z) // Z軸
				{
					sourcePosition.z = mirrorCenter.z - (sourcePosition.z - mirrorCenter.z);
					isMirrored = true;
				}

				var negativeCorner = new int3((int)(sourcePosition.x * (resolution.x - 1)),
				(int)(sourcePosition.y * (resolution.y - 1)),
				(int)(sourcePosition.z * (resolution.z - 1)));

				negativeCorner = max(negativeCorner, new int3(0, 0, 0));
				negativeCorner = min(negativeCorner, resolution - new int3(2, 2, 2));

				// 以下、既存の補間処理と同様...
				// (既存のLatticeJobのインデックス計算と補間処理をここに配置)
				int index0 = (negativeCorner.x + 0) + (negativeCorner.y + 0) * resolution.x +
				(negativeCorner.z + 0) * (resolution.x * resolution.y);
				int index1 = (negativeCorner.x + 1) + (negativeCorner.y + 0) * resolution.x +
				(negativeCorner.z + 0) * (resolution.x * resolution.y);
				int index2 = (negativeCorner.x + 0) + (negativeCorner.y + 1) * resolution.x +
				(negativeCorner.z + 0) * (resolution.x * resolution.y);
				int index3 = (negativeCorner.x + 1) + (negativeCorner.y + 1) * resolution.x +
				(negativeCorner.z + 0) * (resolution.x * resolution.y);
				int index4 = (negativeCorner.x + 0) + (negativeCorner.y + 0) * resolution.x +
				(negativeCorner.z + 1) * (resolution.x * resolution.y);
				int index5 = (negativeCorner.x + 1) + (negativeCorner.y + 0) * resolution.x +
				(negativeCorner.z + 1) * (resolution.x * resolution.y);
				int index6 = (negativeCorner.x + 0) + (negativeCorner.y + 1) * resolution.x +
				(negativeCorner.z + 1) * (resolution.x * resolution.y);
				int index7 = (negativeCorner.x + 1) + (negativeCorner.y + 1) * resolution.x +
				(negativeCorner.z + 1) * (resolution.x * resolution.y);

				var localizedSourcePosition = (sourcePosition) * (resolution - new int3(1, 1, 1)) - negativeCorner;

				// Clamp the local position outside of the bounds so that our interpolation outside the lattice is clamped
				localizedSourcePosition = clamp(localizedSourcePosition, float3.zero, new float3(1, 1, 1));

				var newPosition = float3.zero;

				// X-Axis
				if (sourcePosition.x < 0)
				{
					// Outside of lattice (negative in axis)
					var min1 = lerp(controlPoints[index0].x, controlPoints[index2].x, localizedSourcePosition.y);
					var min2 = lerp(controlPoints[index4].x, controlPoints[index6].x, localizedSourcePosition.y);
					var min = lerp(min1, min2, localizedSourcePosition.z);
					newPosition.x = sourcePosition.x + min;
				}
				else if (sourcePosition.x > 1)
				{
					// Outside of lattice (positive in axis)
					var max1 = lerp(controlPoints[index1].x, controlPoints[index3].x, localizedSourcePosition.y);
					var max2 = lerp(controlPoints[index5].x, controlPoints[index7].x, localizedSourcePosition.y);
					var max = lerp(max1, max2, localizedSourcePosition.z);
					newPosition.x = sourcePosition.x + max - 1;
				}
				else
				{
					// Inside lattice
					var min1 = lerp(controlPoints[index0].x, controlPoints[index2].x, localizedSourcePosition.y);
					var max1 = lerp(controlPoints[index1].x, controlPoints[index3].x, localizedSourcePosition.y);

					var min2 = lerp(controlPoints[index4].x, controlPoints[index6].x, localizedSourcePosition.y);
					var max2 = lerp(controlPoints[index5].x, controlPoints[index7].x, localizedSourcePosition.y);

					var min = lerp(min1, min2, localizedSourcePosition.z);
					var max = lerp(max1, max2, localizedSourcePosition.z);
					newPosition.x = lerp(min, max, localizedSourcePosition.x);
				}

				// Y-Axis
				if (sourcePosition.y < 0)
				{
					// Outside of lattice (negative in axis)
					var min1 = lerp(controlPoints[index0].y, controlPoints[index1].y, localizedSourcePosition.x);
					var min2 = lerp(controlPoints[index4].y, controlPoints[index5].y, localizedSourcePosition.x);
					var min = lerp(min1, min2, localizedSourcePosition.z);
					newPosition.y = sourcePosition.y + min;
				}
				else if (sourcePosition.y > 1)
				{
					// Outside of lattice (positive in axis)
					var max1 = lerp(controlPoints[index2].y, controlPoints[index3].y, localizedSourcePosition.x);
					var max2 = lerp(controlPoints[index6].y, controlPoints[index7].y, localizedSourcePosition.x);
					var max = lerp(max1, max2, localizedSourcePosition.z);
					newPosition.y = sourcePosition.y + max - 1;
				}
				else
				{
					var min1 = lerp(controlPoints[index0].y, controlPoints[index1].y, localizedSourcePosition.x);
					var max1 = lerp(controlPoints[index2].y, controlPoints[index3].y, localizedSourcePosition.x);

					var min2 = lerp(controlPoints[index4].y, controlPoints[index5].y, localizedSourcePosition.x);
					var max2 = lerp(controlPoints[index6].y, controlPoints[index7].y, localizedSourcePosition.x);

					var min = lerp(min1, min2, localizedSourcePosition.z);
					var max = lerp(max1, max2, localizedSourcePosition.z);

					newPosition.y = lerp(min, max, localizedSourcePosition.y);
				}

				// Z-Axis
				if (sourcePosition.z < 0)
				{
					// Outside of lattice (negative in axis)
					var min1 = lerp(controlPoints[index0].z, controlPoints[index1].z, localizedSourcePosition.x);
					var min2 = lerp(controlPoints[index2].z, controlPoints[index3].z, localizedSourcePosition.x);
					var min = lerp(min1, min2, localizedSourcePosition.y);
					newPosition.z = sourcePosition.z + min;
				}
				else if (sourcePosition.z > 1)
				{
					// Outside of lattice (positive in axis)
					var max1 = lerp(controlPoints[index4].z, controlPoints[index5].z, localizedSourcePosition.x);
					var max2 = lerp(controlPoints[index6].z, controlPoints[index7].z, localizedSourcePosition.x);
					var max = lerp(max1, max2, localizedSourcePosition.y);
					newPosition.z = sourcePosition.z + max - 1;
				}
				else
				{
					var min1 = lerp(controlPoints[index0].z, controlPoints[index1].z, localizedSourcePosition.x);
					var max1 = lerp(controlPoints[index4].z, controlPoints[index5].z, localizedSourcePosition.x);

					var min2 = lerp(controlPoints[index2].z, controlPoints[index3].z, localizedSourcePosition.x);
					var max2 = lerp(controlPoints[index6].z, controlPoints[index7].z, localizedSourcePosition.x);

					var min = lerp(min1, min2, localizedSourcePosition.y);
					var max = lerp(max1, max2, localizedSourcePosition.y);

					newPosition.z = lerp(min, max, localizedSourcePosition.z);
				}
				//(ここまで)
                
				// ミラーリングされた頂点の場合のみ、変形後の位置を対称的に配置
				if (isMirrored)
				{
					if ((mirrorAxis & 1) != 0 && originalPosition.x > mirrorCenter.x)
					{
						// 中心からの距離を保持したまま反転
						float distanceFromCenter = abs(newPosition.x - 0.5f);
						newPosition.x = - newPosition.x;
					}
					if ((mirrorAxis & 2) != 0 && originalPosition.y > mirrorCenter.y)
					{
						float distanceFromCenter = abs(newPosition.y - 0.5f);
						newPosition.y = - newPosition.y;
					}
					if ((mirrorAxis & 4) != 0 && originalPosition.z > mirrorCenter.z)
					{
						float distanceFromCenter = abs(newPosition.z - 0.5f);
						newPosition.z = - newPosition.z;
					}
				}

				vertices[index] = transform(targetToMesh, newPosition);
			}
		}
	}
}