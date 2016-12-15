using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Linq;
using UnityEngine;
using UObject = UnityEngine.Object;

public class UnityHeapDump
{
	const int TYPE_MIN_SIZE_TO_PRINT = 1;
	const int ROOT_MIN_SIZE = 1;
	const int CHILD_MIN_SIZE = 1;

	static int ThreadJobsPrinting = 0;
	static int ThreadJobsDone = 0;

	static HashSet<Type> genericTypes = new HashSet<Type>();

	public static void Create ()
	{
		TypeData.Start();
		ThreadJobsPrinting = 0;
		ThreadJobsDone = 0;

		if (Directory.Exists("dump"))
		{
			Directory.Delete("dump", true);
		}
		Directory.CreateDirectory("dump");
		Directory.CreateDirectory("dump/sobjects");
		Directory.CreateDirectory("dump/uobjects");
		Directory.CreateDirectory("dump/statics");

		using (var logger = new StreamWriter("dump/log.txt"))
		{
			Dictionary<Assembly, List<StructOrClass>> assemblyResults = new Dictionary<Assembly, List<StructOrClass>>();
			Dictionary<Assembly, int> assemblySizes = new Dictionary<Assembly, int>();
			List<KeyValuePair<Type, Exception>> parseErrors = new List<KeyValuePair<Type, Exception>>();
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				string assemblyFolder;
				if (assembly.FullName.Contains("Assembly-CSharp"))
				{
					assemblyFolder = string.Format("dump/statics/{0}/", assembly.FullName.Replace("<", "(").Replace(">", ")").Replace(".", "_"));
				} else
				{
					assemblyFolder = "dump/statics/misc/";
				}
				Directory.CreateDirectory(assemblyFolder);
				List<StructOrClass> types = new List<StructOrClass>();
				foreach (var type in assembly.GetTypes())
				{
					if (type.IsEnum || type.IsGenericType)
					{
						continue;
					}
					try
					{
						types.Add(new StructOrClass(type, assemblyFolder));
					}
					catch (Exception e)
					{
						parseErrors.Add (new KeyValuePair<Type, Exception>(type, e));
					}
				}
				assemblyResults[assembly] = types;
			}

			List<StructOrClass> unityComponents = new List<StructOrClass>();

			foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
			{
				foreach (var component in go.GetComponents<Component>())
				{
					if (component == null)
					{
						continue;
					}
					try
					{
						unityComponents.Add(new StructOrClass(component));
					}
					catch (Exception e)
					{
						parseErrors.Add(new KeyValuePair<Type, Exception>(component.GetType(), e));
					}
				}
			}

			List<StructOrClass> unityScriptableObjects = new List<StructOrClass>();

			foreach (ScriptableObject scriptableObject in Resources.FindObjectsOfTypeAll<ScriptableObject>())
			{
				if (scriptableObject == null)
				{
					continue;
				}
				try
				{
					unityScriptableObjects.Add(new StructOrClass(scriptableObject));
				}
				catch (Exception e)
				{
					parseErrors.Add(new KeyValuePair<Type, Exception>(scriptableObject.GetType(), e));
				}
			}

			foreach (var genericType in genericTypes.ToList())
			{
				try
				{
					assemblyResults[genericType.Assembly].Add(new StructOrClass(genericType, "dump/statics/misc/"));
				}
				catch (Exception e)
				{
					parseErrors.Add(new KeyValuePair<Type, Exception>(genericType, e));
				}
			}

			foreach (var pair in assemblyResults)
			{
				assemblySizes[pair.Key] = pair.Value.Sum(a => a.Size);
				pair.Value.Sort((a, b) => b.Size - a.Size);
			}

			TypeData.Clear();

			var assemblySizesList = assemblySizes.ToList();
			assemblySizesList.Sort((a, b) => (b.Value - a.Value));

			unityComponents.Sort((a, b) => (b.Size - a.Size));
			int unityComponentsSize = unityComponents.Sum(a => a.Size);
			bool printedUnityComponents = false;

			unityScriptableObjects.Sort((a, b) => (b.Size - a.Size));
			int unityScriptableObjectsSize = unityScriptableObjects.Sum(a => a.Size);
			bool printedUnityScriptableObjects = false;

			logger.WriteLine("Total tracked memory (including duplicates, so too high) = {0}", assemblySizesList.Sum(a => a.Value) + unityComponentsSize + unityScriptableObjectsSize);


			foreach (var pair in assemblySizesList)
			{
				var assembly = pair.Key;
				var size = pair.Value;

				if (!printedUnityComponents && size < unityComponentsSize)
				{
					printedUnityComponents = true;
					logger.WriteLine("Unity components of total size: {0}", unityComponentsSize);
					foreach (var instance in unityComponents)
					{
						if (instance.Size >= TYPE_MIN_SIZE_TO_PRINT)
						{
							logger.WriteLine("    Type {0} (ID: {1}) of size {2}", instance.ParsedType.FullName, instance.InstanceID, instance.Size);
						}
					}
				}

				if (!printedUnityScriptableObjects && size < unityScriptableObjectsSize)
				{
					printedUnityScriptableObjects = true;
					logger.WriteLine("Unity scriptableobjects of total size: {0}", unityScriptableObjectsSize);
					foreach (var instance in unityScriptableObjects)
					{
						if (instance.Size >= TYPE_MIN_SIZE_TO_PRINT)
						{
							logger.WriteLine("    Type {0} (ID: {1}) of size {2}", instance.ParsedType.FullName, instance.InstanceID, instance.Size);
						}
					}
				}

				logger.WriteLine("Assembly: {0} of total size: {1}", assembly, size);
				foreach (var type in assemblyResults[assembly])
				{
					if (type.Size >= TYPE_MIN_SIZE_TO_PRINT)
					{
						logger.WriteLine("    Type: {0} of size {1}", type.ParsedType.FullName, type.Size);
					}
				}
			}
			foreach (var error in parseErrors)
			{
				logger.WriteLine(error);
			}
		}

		while (ThreadJobsDone < ThreadJobsPrinting)
		{
			System.Threading.Thread.Sleep(1);
		}
	}

	class StructOrClass 
	{
		public int Size { get; private set; }
		public Type ParsedType { get; private set; }
		public int InstanceID { get; private set; }
		int ArraySize { get; set; }
		string Identifier { get; set; }

		List<StructOrClass> Children = new List<StructOrClass>();

		/// <summary>
		/// Parse static types
		/// </summary>
		public StructOrClass (Type type, string assemblyFolder)
		{
			ParsedType = type;
			HashSet<object> seenObjects = new HashSet<object>();
			Identifier = type.FullName;
			foreach (var fieldInfo in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				ParseField(fieldInfo, null, seenObjects);
			}
			if (Size < ROOT_MIN_SIZE)
			{
				return;
			}

			System.Threading.Interlocked.Increment(ref ThreadJobsPrinting);
			System.Threading.ThreadPool.QueueUserWorkItem (obj => {
				try
				{
					string fileName = Identifier.Replace("<", "(").Replace(">", ")").Replace(".", "_");
					using (var writer = new StreamWriter(string.Format("{0}{1}-{2}", assemblyFolder, Size, fileName)))
					{
						writer.WriteLine("Static ({0}): {1} bytes", ParsedType, Size);
						Children.Sort((a, b) => b.Size - a.Size);
						string childIndent = "    ";
						foreach (var child in Children)
						{
							if (child.Size >= CHILD_MIN_SIZE)
							{
								child.Write(writer, childIndent);
							} else
							{
								break;
							}
						}
					}
				}
				finally
				{
					System.Threading.Interlocked.Increment(ref ThreadJobsDone);
				}
			});
		}

		/// <summary>
		/// Parse monobehaviour and scriptableobject instances
		/// </summary>
		public StructOrClass (UObject uObject)
		{
			InstanceID = uObject.GetInstanceID();
			ParsedType = uObject.GetType();
			Identifier = uObject.name + uObject.GetInstanceID();
			HashSet<object> seenObjects = new HashSet<object>();

			foreach (var field in ParsedType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				ParseField(field, uObject, seenObjects);
			}

			if (Size < ROOT_MIN_SIZE)
			{
				return;
			}

			string fileName = string.Format("dump/{0}/{1}-{2}",
				uObject is ScriptableObject ? "sobjects" : "uobjects",
				Size,
				Identifier.Replace("<", "(").Replace(">", ")").Replace(".", "_")
			);

			using (var writer = new StreamWriter(fileName))
			{
				writer.WriteLine("{0} ({1}): {2} bytes", Identifier, ParsedType, Size);
				Children.Sort((a, b) => b.Size - a.Size);
				foreach (var child in Children)
				{
					if (child.Size >= CHILD_MIN_SIZE)
					{
						child.Write(writer, "    ");
					}
				}
			}
		}

		/// <summary>
		/// Parse field objects; only called for arrays, references and structs with references in them
		/// </summary>
		StructOrClass(string name, object root, TypeData rootTypeData, HashSet<object> seenObjects)
		{
			Identifier = name;
			ParsedType = root.GetType();
			Size = rootTypeData.Size;
			if (ParsedType.IsArray)
			{
				int i = 0;
				ArraySize = GetTotalLength((Array)root);
				Type elementType = ParsedType.GetElementType();
				TypeData elementTypeData = TypeData.Get(elementType);
				if (elementType.IsValueType || elementType.IsPrimitive || elementType.IsEnum)
				{
					if (elementTypeData.DynamicSizedFields == null)
					{
						Size += elementTypeData.Size * ArraySize;
						return;
					}

					foreach (var item in (Array)root)
					{
						StructOrClass child = new StructOrClass((i++).ToString(), item, elementTypeData, seenObjects);
						Size += child.Size;
						Children.Add(child);
					}
				}
				else
				{
					Size += IntPtr.Size * ArraySize;
					foreach (var item in (Array)root)
					{
						ParseItem(item, (i++).ToString(), seenObjects);
					}
				}
			} else
			{
				if (rootTypeData.DynamicSizedFields != null)
				{
					foreach (var fieldInfo in rootTypeData.DynamicSizedFields)
					{
						ParseField(fieldInfo, root, seenObjects);
					}
				}
			}
		}

		/// <summary>
		/// Parse the field of the object, ignoring any seenObjects. If root is null, it is considered a static field.
		/// </summary>
		void ParseField(FieldInfo fieldInfo, object root, HashSet<object> seenObjects)
		{
			if (!fieldInfo.FieldType.IsPointer)
			{
				ParseItem(fieldInfo.GetValue(root), fieldInfo.Name, seenObjects);
			}
		}

		void ParseItem (object obj, string objName, HashSet<object> seenObjects)
		{
			if (obj == null)
			{
				return;
			}
			Type type = obj.GetType();
			if (type.IsPointer)
			{
				return; // again, a pointer cast to whatever the fieldtype is, shoo.
			}
			if (type == typeof(string))
			{
				// string needs special handling
				int strSize = 3 * IntPtr.Size + 2;
				strSize += ((string)(obj)).Length * sizeof(char);
				int pad = strSize % IntPtr.Size;
				if (pad != 0)
				{
					strSize += IntPtr.Size - pad;
				}
				Size += strSize;
				return;
			}
			// obj is not null, and a primitive/enum/array/struct/class
			TypeData fieldTypeData = TypeData.Get(type);
			if (type.IsClass || type.IsArray || fieldTypeData.DynamicSizedFields != null)
			{
				// class, array, or struct with pointers
				if (!(type.IsPrimitive || type.IsValueType || type.IsEnum))
				{
					if (!seenObjects.Add(obj))
					{
						return;
					}
				}

				StructOrClass child = new StructOrClass(objName, obj, fieldTypeData, seenObjects);
				Size += child.Size;
				Children.Add(child);
				return;
			}
			else
			{
				// primitive, enum, or a struct without pointers, embed it in parent
				Size += fieldTypeData.Size;
			}
		}

		void Write(StreamWriter writer, string indent)
		{
			if (ParsedType.IsArray)
			{
				writer.WriteLine("{0}{1} ({2}:{3}) : {4}", indent, Identifier, ParsedType, ArraySize, Size);
			} else
			{
				writer.WriteLine("{0}{1} ({2}) : {3}", indent, Identifier, ParsedType, Size);
			}
			Children.Sort((a, b) => b.Size - a.Size);
			string childIndent = indent + "    ";
			foreach (var child in Children)
			{
				if (child.Size >= CHILD_MIN_SIZE)
				{
					child.Write(writer, childIndent);
				} else
				{
					return;
				}
			}
		}

		static int GetTotalLength(Array val)
		{
			int sum = val.GetLength(0);
			for (int i = 1; i < val.Rank; i++)
			{
				sum *= val.GetLength(i);
			}
			return sum;
		}
	}

	public class TypeData
	{
		public int Size { get; private set; }
		public List<FieldInfo> DynamicSizedFields { get; private set; }

		static Dictionary<Type, TypeData> seenTypeData;
		static Dictionary<Type, TypeData> seenTypeDataNested;

		public static void Clear()
		{
			seenTypeData = null;
		}

		public static void Start()
		{
			seenTypeData = new Dictionary<Type, TypeData>();
			seenTypeDataNested = new Dictionary<Type, TypeData>();
		}

		public static TypeData Get(Type type)
		{
			TypeData data;
			if (!seenTypeData.TryGetValue(type, out data))
			{
				data = new TypeData(type);
				seenTypeData[type] = data;
			}
			return data;
		}

		public static TypeData GetNested(Type type)
		{
			TypeData data;
			if (!seenTypeDataNested.TryGetValue(type, out data))
			{
				data = new TypeData(type, true);
				seenTypeDataNested[type] = data;
			}
			return data;
		}

		public TypeData(Type type, bool nested = false)
		{
			if (type.IsGenericType)
			{
				genericTypes.Add(type);
			}
			Type baseType = type.BaseType;
			if (baseType != null
				&& baseType != typeof(object)
				&& baseType != typeof(ValueType)
				&& baseType != typeof(Array)
				&& baseType != typeof(Enum))
			{
				TypeData baseTypeData = GetNested(baseType);
				Size += baseTypeData.Size;

				if (baseTypeData.DynamicSizedFields != null)
				{
					DynamicSizedFields = new List<FieldInfo>(baseTypeData.DynamicSizedFields);
				}
			}
			if (type.IsPointer)
			{
				Size = IntPtr.Size;
			}
			else if (type.IsArray)
			{
				Type elementType = type.GetElementType();
				Size = ((elementType.IsValueType || elementType.IsPrimitive || elementType.IsEnum) ? 3 : 4) * IntPtr.Size;
			}
			else if (type.IsPrimitive)
			{
				Size = Marshal.SizeOf(type);
			}
			else if (type.IsEnum)
			{
				Size = Marshal.SizeOf(Enum.GetUnderlyingType(type));
			}
			else // struct, class
			{
				if (!nested && type.IsClass)
				{
					Size = 2 * IntPtr.Size;
				}
				foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					ProcessField(field, field.FieldType);
				}
				if (!nested && type.IsClass)
				{
					Size = Math.Max(3 * IntPtr.Size, Size);
					int pad = Size % IntPtr.Size;
					if (pad != 0)
					{
						Size += IntPtr.Size - pad;
					}
				}
			}
		}

		void ProcessField(FieldInfo field, Type fieldType)
		{
			if (IsStaticallySized(fieldType))
			{
				Size += GetStaticSize(fieldType);
			}
			else
			{
				if (!(fieldType.IsValueType || fieldType.IsPrimitive || fieldType.IsEnum))
				{
					Size += IntPtr.Size;
				}
				if (fieldType.IsPointer)
				{
					return;
				}
				if (DynamicSizedFields == null)
				{
					DynamicSizedFields = new List<FieldInfo>();
				}
				DynamicSizedFields.Add(field);
			}
		}

		static bool IsStaticallySized(Type type)
		{

			if (type.IsPointer || type.IsArray || type.IsClass || type.IsInterface)
			{
				return false;
			}
			if (type.IsPrimitive || type.IsEnum)
			{
				return true;
			}
			foreach (var nestedField in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (!IsStaticallySized(nestedField.FieldType))
				{
					return false;
				}
			}
			return true;
		}

		/// <summary>
		/// Gets size of type. Assumes IsStaticallySized (type) is true. (primitive, enum, {struct or class with no references in it})
		/// </summary>
		static int GetStaticSize(Type type)
		{
			if (type.IsPrimitive)
			{
				return Marshal.SizeOf(type);
			}
			if (type.IsEnum)
			{
				return Marshal.SizeOf(Enum.GetUnderlyingType(type));
			}
			int size = 0;
			foreach (var nestedField in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				size += GetStaticSize(nestedField.FieldType);
			}
			return size;
		}
	}
}