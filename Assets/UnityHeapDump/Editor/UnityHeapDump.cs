using UnityEditor;

class HeapDumping
{
	[MenuItem("Tools/Memory/Dump Heap")]
	public static void DumpIt ()
	{
		UnityHeapDump.Create();
	}
}
