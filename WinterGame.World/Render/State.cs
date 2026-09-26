using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Graphics2.Mathematics;
// ReSharper disable FieldCanBeMadeReadOnly.Local

namespace WinterGame.World.Render;

public sealed class MultiLabelSurfaceNetsStateStorage
{
    public readonly Vector3i CellCount;
    public readonly int RelaxHalo;
    
    private readonly int[] _cellIndices;
    private readonly Vector3i[] _localCellsList;
    private readonly Vector3[] _cellCentersList;
    private readonly MultiLabelSurfaceNets.VertexType[] _vertexTypesList;
    private readonly MultiLabelSurfaceNets.FaceCrossingType[] _faceTypesList;
    private readonly int[] _edgeMasksList;
    private readonly bool[] _solidBasesList;
    private readonly uint[] _edgeMaterialsList;
    private readonly Vector3[] _current;
    private readonly Vector3[] _next;
    private readonly int[] _remap;
    private readonly int[] _touchedCells;
    private readonly int[] _neighborIndices;
    
    public MultiLabelSurfaceNetsStateStorage(Vector3i cellCount, int relaxHalo)
    {
        CellCount = cellCount;
        RelaxHalo = relaxHalo;
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCount.X, 1, nameof(cellCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCount.Y, 1, nameof(cellCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCount.Z, 1, nameof(cellCount));
        ArgumentOutOfRangeException.ThrowIfNegative(relaxHalo);

        var vertexRangeX = cellCount.X + 2 * relaxHalo + 1;
        var vertexRangeY = cellCount.Y + 2 * relaxHalo + 1;
        var vertexRangeZ = cellCount.Z + 2 * relaxHalo + 1;

        var maxBaseVertices = vertexRangeX * vertexRangeY * vertexRangeZ;
        var emitCells = cellCount.X * cellCount.Y * cellCount.Z;
       
        // Two duplicate vertices per emitted cell are the material junction copies, and three more are the concave quad fan apexes:
        var maxVertices = maxBaseVertices + 5 * emitCells;

        _cellIndices = new int[maxBaseVertices];
        _localCellsList = new Vector3i[maxVertices];
        _cellCentersList = new Vector3[maxBaseVertices];
        _vertexTypesList = new MultiLabelSurfaceNets.VertexType[maxBaseVertices];
        _faceTypesList = new MultiLabelSurfaceNets.FaceCrossingType[maxBaseVertices * MultiLabelSurfaceNets.FaceCount];
        _edgeMasksList = new int[maxBaseVertices];
        _solidBasesList = new bool[maxBaseVertices];
        _edgeMaterialsList = new uint[maxBaseVertices * MultiLabelSurfaceNets.BaseEdgeCount];
        _current = new Vector3[maxBaseVertices];
        _next = new Vector3[maxBaseVertices];
        _remap = new int[maxVertices];
        _touchedCells = new int[maxBaseVertices];
        _neighborIndices = new int[maxBaseVertices * MultiLabelSurfaceNets.FaceCount];

        // The cell map stays -1 between calls because every write records its flat index and Dispose clears it again.
        Array.Fill(_cellIndices, -1);
    }

    public State Begin()
    {
        return new State
        {
            CellIndicesStore = _cellIndices,
            LocalCellsListStore = _localCellsList,
            CellCentersListStore = _cellCentersList,
            VertexTypesListStore = _vertexTypesList,
            FaceTypesListStore = _faceTypesList,
            EdgeMasksListStore = _edgeMasksList,
            SolidBasesListStore = _solidBasesList,
            EdgeMaterialsListStore = _edgeMaterialsList,
            CurrentStore = _current,
            NextStore = _next,
            RemapStore = _remap,
            TouchedCells = _touchedCells,
            NeighborIndicesStore = _neighborIndices
        };
    }
    
    public struct State
    {
        internal int[] CellIndicesStore;
        internal Vector3i[] LocalCellsListStore;
        internal Vector3[] CellCentersListStore;
        internal MultiLabelSurfaceNets.VertexType[] VertexTypesListStore;
        internal MultiLabelSurfaceNets.FaceCrossingType[] FaceTypesListStore;
        internal int[] EdgeMasksListStore;
        internal bool[] SolidBasesListStore;
        internal uint[] EdgeMaterialsListStore;
        internal Vector3[] CurrentStore;
        internal Vector3[] NextStore;
        internal int[] RemapStore;
        internal int[] TouchedCells;
        internal int[] NeighborIndicesStore;
        internal int TouchedCount;

        public int LocalCellsCount;
        public int CellCentersCount;
        public int VertexTypesCount;
        public int FaceTypesCount;
        public int EdgeMasksCount;
        public int SolidBasesCount;
        public int EdgeMaterialsCount;
        
        #region Indexers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref int CellIndex(int index)
        {
            Debug.Assert((uint)index < (uint)CellIndicesStore.Length, "Tried to access a cell index out of range");
            return ref CellIndicesStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetCellIndex(int index, int value)
        {
            Debug.Assert((uint)index < (uint)CellIndicesStore.Length, "Tried to write a cell index out of range");
            Debug.Assert((uint)TouchedCount < (uint)TouchedCells.Length, "Touched cell list overflow");

            CellIndicesStore[index] = value;
            TouchedCells[TouchedCount++] = index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Vector3i LocalCells(int index)
        {
            Debug.Assert((uint)index < (uint)LocalCellsCount, "Tried to read local cell out-of-range");
            return ref LocalCellsListStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Vector3 CellCenter(int index)
        {
            Debug.Assert((uint)index < (uint)CellCentersCount, "Tried to read cell center out-of-range");
            return ref CellCentersListStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref MultiLabelSurfaceNets.VertexType VertexType(int index)
        {
            Debug.Assert((uint)index < (uint)VertexTypesCount, "Tried to read vertex type out-of-range");
            return ref VertexTypesListStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref MultiLabelSurfaceNets.FaceCrossingType FaceType(int index)
        {
            Debug.Assert((uint)index < (uint)FaceTypesCount, "Tried to read face type out-of-range");
            return ref FaceTypesListStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref int EdgeMask(int index)
        {
            Debug.Assert((uint)index < (uint)EdgeMasksCount, "Tried to read edge mask out-of-range");
            return ref EdgeMasksListStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref bool SolidBase(int index)
        {
            Debug.Assert((uint)index < (uint)SolidBasesCount, "Tried to read solid base out-of-range");
            return ref SolidBasesListStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref uint EdgeMaterial(int index)
        {
            Debug.Assert((uint)index < (uint)EdgeMaterialsCount, "Tried to read edge material out-of-range");
            return ref EdgeMaterialsListStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Vector3 Current(int index)
        {
            Debug.Assert((uint)index < (uint)CurrentStore.Length, "Tried to read the current relaxation position out-of-range");
            return ref CurrentStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Vector3 Next(int index)
        {
            Debug.Assert((uint)index < (uint)NextStore.Length, "Tried to write the next relaxation position out-of-range");
            return ref NextStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref int Remap(int index)
        {
            Debug.Assert((uint)index < (uint)RemapStore.Length, "Tried to read a compact remap out-of-range");
            return ref RemapStore[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref int NeighborIndex(int index)
        {
            Debug.Assert((uint)index < (uint)NeighborIndicesStore.Length, "Tried to read a relax neighbor out-of-range");
            return ref NeighborIndicesStore[index];
        }

        #endregion

        #region Lifetime
        
        public void Dispose()
        {
            for (var index = 0; index < TouchedCount; index++)
            {
                CellIndicesStore[TouchedCells[index]] = -1;
            }

            TouchedCount = 0;
        }

        #endregion

        #region Relaxation

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FlipRelaxation()
        {
            (CurrentStore, NextStore) = (NextStore, CurrentStore);
        }

        #endregion

        #region List Add

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddLocalCell(in Vector3i value)
        {
            Debug.Assert((uint)LocalCellsCount < (uint)LocalCellsListStore.Length, "Local cells list overflow");
            LocalCellsListStore[LocalCellsCount++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddCellCenter(in Vector3 value)
        {
            Debug.Assert((uint)CellCentersCount < (uint)CellCentersListStore.Length, "Cell centers list overflow");
            CellCentersListStore[CellCentersCount++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddVertexType(MultiLabelSurfaceNets.VertexType value)
        {
            Debug.Assert((uint)VertexTypesCount < (uint)VertexTypesListStore.Length, "Vertex types list overflow");
            VertexTypesListStore[VertexTypesCount++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddFaceType(MultiLabelSurfaceNets.FaceCrossingType value)
        {
            Debug.Assert((uint)FaceTypesCount < (uint)FaceTypesListStore.Length, "Face types list overflow");
            FaceTypesListStore[FaceTypesCount++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddEdgeMask(int value)
        {
            Debug.Assert((uint)EdgeMasksCount < (uint)EdgeMasksListStore.Length, "Edge masks list overflow");
            EdgeMasksListStore[EdgeMasksCount++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddSolidBase(bool value)
        {
            Debug.Assert((uint)SolidBasesCount < (uint)SolidBasesListStore.Length, "Solid bases list overflow");
            SolidBasesListStore[SolidBasesCount++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddEdgeMaterial(uint value)
        {
            Debug.Assert((uint)EdgeMaterialsCount < (uint)EdgeMaterialsListStore.Length, "Edge materials list overflow");
            EdgeMaterialsListStore[EdgeMaterialsCount++] = value;
        }

        #endregion
    }
}
