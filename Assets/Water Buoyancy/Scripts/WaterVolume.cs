﻿using UnityEngine;
using System.Collections.Generic;
using WaterBuoyancy.Collections;

namespace WaterBuoyancy
{
    [RequireComponent(typeof(BoxCollider))]
    [RequireComponent(typeof(MeshFilter))]
    public class WaterVolume : MonoBehaviour
    {
        public const string TAG = "Water Volume";

        [SerializeField]
        private float density = 1f;

        [SerializeField]
        private int rows = 10;

        [SerializeField]
        private int columns = 10;

        [SerializeField]
        private float quadSegmentSize = 1f;

        [SerializeField]
        private bool autoUpdateWaterMesh = true;

        //[SerializeField]
        //private Transform debugTrans; // Only for debugging

        private Mesh mesh;
        private List<Vector3> meshLocalVertices;
        private Vector3[] meshWorldVertices;

        public float Density
        {
            get
            {
                return this.density;
            }
        }

        public int Rows
        {
            get
            {
                return this.rows;
            }
        }

        public int Columns
        {
            get
            {
                return this.columns;
            }
        }

        public float QuadSegmentSize
        {
            get
            {
                return this.quadSegmentSize;
            }
        }

        public Mesh Mesh
        {
            get
            {
                if (this.mesh == null)
                {
                    this.mesh = this.GetComponent<MeshFilter>().mesh;
                }

                return this.mesh;
            }
        }

        private void Awake()
        {
            initVertices();
            updateMeshWorldVertices();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.matrix = this.transform.localToWorldMatrix;

            Gizmos.DrawWireCube(this.GetComponent<BoxCollider>().center, this.GetComponent<BoxCollider>().size);
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
            {
                Gizmos.color = Color.cyan - new Color(0f, 0f, 0f, 0.75f);
                Gizmos.matrix = this.transform.localToWorldMatrix;

                Gizmos.DrawCube(this.GetComponent<BoxCollider>().center - Vector3.up * 0.01f, this.GetComponent<BoxCollider>().size);

                Gizmos.color = Color.cyan - new Color(0f, 0f, 0f, 0.5f);
                Gizmos.DrawWireCube(this.GetComponent<BoxCollider>().center, this.GetComponent<BoxCollider>().size);

                Gizmos.matrix = Matrix4x4.identity;
            }
            else
            {
                // Draw sufrace normal
                //var vertices = this.meshWorldVertices;
                //var triangles = this.Mesh.triangles;
                //for (int i = 0; i < triangles.Length; i += 3)
                //{
                //    Gizmos.color = Color.white;
                //    Gizmos.DrawLine(vertices[triangles[i + 0]], vertices[triangles[i + 1]]);
                //    Gizmos.DrawLine(vertices[triangles[i + 1]], vertices[triangles[i + 2]]);
                //    Gizmos.DrawLine(vertices[triangles[i + 2]], vertices[triangles[i + 0]]);

                //    Vector3 center = MathfUtils.GetAveratePoint(vertices[triangles[i + 0]], vertices[triangles[i + 1]], vertices[triangles[i + 2]]);
                //    Vector3 normal = this.GetSurfaceNormal(center);

                //    Gizmos.color = Color.green;
                //    Gizmos.DrawLine(center, center + normal);
                //}

                // Draw mesh vertices
                //if (this.meshWorldVertices != null)
                //{
                //    for (int i = 0; i < this.meshWorldVertices.Length; i++)
                //    {
                //        DebugUtils.DrawPoint(this.meshWorldVertices[i], Color.red);
                //    }
                //}

                // Test GetSurroundingTrianglePolygon(Vector3 worldPoint);
                //if (debugTrans != null)
                //{
                //    Gizmos.color = Color.blue;
                //    Gizmos.DrawSphere(debugTrans.position, 0.1f);

                //    var point = debugTrans.position;
                //    var triangle = this.GetSurroundingTrianglePolygon(point);
                //    if (triangle != null)
                //    {
                //        Gizmos.color = Color.red;

                //        Gizmos.DrawLine(triangle[0], triangle[1]);
                //        Gizmos.DrawLine(triangle[1], triangle[2]);
                //        Gizmos.DrawLine(triangle[2], triangle[0]);
                //    }
                //}
            }
        }

        private void Update()
        {
            if (autoUpdateWaterMesh == false)
                return;

            updateMeshWorldVertices();
        }

        private bool GetSurroundingTrianglePolygon(Vector3 worldPoint, ref Vector3[] trianglePolygon)
        {
            Vector3 localPoint = this.transform.InverseTransformPoint(worldPoint);
            int x = Mathf.CeilToInt(localPoint.x / this.QuadSegmentSize);
            int z = Mathf.CeilToInt(localPoint.z / this.QuadSegmentSize);
            if (x <= 0 || z <= 0 || x >= (this.Columns + 1) || z >= (this.Rows + 1))
            {
                return false;
            }

            if ((worldPoint - this.meshWorldVertices[this.GetIndex(z, x)]).sqrMagnitude <
                ((worldPoint - this.meshWorldVertices[this.GetIndex(z - 1, x - 1)]).sqrMagnitude))
            {
                trianglePolygon[0] = this.meshWorldVertices[this.GetIndex(z, x)];
            }
            else
            {
                trianglePolygon[0] = this.meshWorldVertices[this.GetIndex(z - 1, x - 1)];
            }

            trianglePolygon[1] = this.meshWorldVertices[this.GetIndex(z - 1, x)];
            trianglePolygon[2] = this.meshWorldVertices[this.GetIndex(z, x - 1)];

            return true;
        }

        Vector3[] m_meshPolygon = new Vector3[3];

        public Vector3 GetSurfaceNormal(Vector3 worldPoint)
        {
            if (GetSurroundingTrianglePolygon(worldPoint, ref m_meshPolygon))
            {
                Vector3 planeV1 = m_meshPolygon[1] - m_meshPolygon[0];
                Vector3 planeV2 = m_meshPolygon[2] - m_meshPolygon[0];
                Vector3 planeNormal = Vector3.Cross(planeV1, planeV2).normalized;
                if (planeNormal.y < 0f)
                {
                    planeNormal *= -1f;
                }
            }

            return this.transform.up;
        }

        public float GetWaterLevel(Vector3 worldPoint)
        {
            if (GetSurroundingTrianglePolygon(worldPoint, ref m_meshPolygon))
            {
                Vector3 planeV1 = m_meshPolygon[1] - m_meshPolygon[0];
                Vector3 planeV2 = m_meshPolygon[2] - m_meshPolygon[0];
                Vector3 planeNormal = Vector3.Cross(planeV1, planeV2).normalized;
                if (planeNormal.y < 0f)
                {
                    planeNormal *= -1f;
                }

                // Plane equation
                float yOnWaterSurface = (-(worldPoint.x * planeNormal.x) - (worldPoint.z * planeNormal.z) + Vector3.Dot(m_meshPolygon[0], planeNormal)) / planeNormal.y;
                //Vector3 pointOnWaterSurface = new Vector3(point.x, yOnWaterSurface, point.z);
                //DebugUtils.DrawPoint(pointOnWaterSurface, Color.magenta);

                return yOnWaterSurface;
            }

            return this.transform.position.y;
        }

        private bool IsPointUnderWater(Vector3 worldPoint)
        {
            return this.GetWaterLevel(worldPoint) - worldPoint.y > 0f;
        }

        private int GetIndex(int row, int column)
        {
            return row * (this.Columns + 1) + column;
        }

        private void initVertices()
        {
            meshLocalVertices = new List<Vector3>(Mesh.vertexCount);
            Mesh.GetVertices(meshLocalVertices);
            meshWorldVertices = new Vector3[meshLocalVertices.Count];
        }

        private void updateMeshWorldVertices()
        {
            Mesh.GetVertices(meshLocalVertices);
            ConvertPointsToWorldSpace(meshLocalVertices, ref meshWorldVertices);
        }

        private void ConvertPointsToWorldSpace(List<Vector3> localPoints, ref Vector3[] worldPoints)
        {
            for (int i = 0; i < localPoints.Count; i++)
            {
                worldPoints[i] = transform.TransformPoint(localPoints[i]);
            }
        }
    }
}
