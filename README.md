# UnityHeapDump
Tool to dump memory to text files for inspection

Primary use is to find which static fields hold how much memory in order to find possible object leaks. For leaking of unity objects, please use the detailed memory profiler included in Unity3D. 
Total tracked memory found by this script can be much higher than the heap size, as an object with shared ownership will be counted towards every owner.

Use the menu item "Tools/Memory/Dump Heap" or call UnityHeapDump.Create() somewhere from your code to create a memory dump at %projectroot%/dump/

# Output format

Example of a small class with 3 static fields:
```
Static (Players): 720 bytes
    loadedPlayers (System.Collections.Generic.Dictionary`2[NetworkID,Players+Player]) : 588
        valueSlots (Players+Player[]:12) : 184
            0 (Players+Player) : 56
        keySlots (NetworkID[]:12) : 132
        linkSlots (System.Collections.Generic.Link[]:12) : 120
        table (System.Int32[]:12) : 72
    connectedPlayers (System.Collections.Generic.List 1[Players+Player]) : 104
        _items (Players+Player[]:4) : 72
    PlayerHasID (System.Func 3[Players+Player,NetworkID,System.Boolean]) : 24
```

A summary of the results is available at %projectroot%/dump/log.txt. This contains all Types and Components parsed and Errors at the end. The summary lists Types in order of size, by order of total Assembly size.

* Unity3D code Assemblies are printed to a file in the form of %projectroot%/dump/statics/%assembly%/%size%-%type%.
* Other Assemblies are printed to a file at %projectroot%/dump/statics/misc/%size%-%type%.
* Unity object results are printed to %projectroot%/dump/uobjects/%size%-%type%
* Unity scriptableObject results to %projectroot%/dump/sobjects/%size%-%type%

Every file starts with the Types total summed size or all static references (or instance in case of unity objects). 

* A 'normal' reference is printed in the following format: %fieldName% (%type%) : %totalSize%.
* An Array is printed like: %fieldName% (%type%:%arraySize%) : %totalSize%. An array item has its index as %fieldName%.

Indented afterwards are all object fields associated with the instance. Value type fields are not listed individually, but their size is included in the parent. Unless the value type is a struct with references to objects; those are listed.

# Settings

Not much, but there are 3 constants in the main file;
TYPE_MIN_SIZE_TO_PRINT, minimum byte count required before the type is printed to log.txt
ROOT_MIN_SIZE, minimum byte count to create a file for the type
CHILD_MIN_SIZE, minimum byte count to print the reference itself inside of the file (this is the main variable to tweak for scaling output size)

# Issues

Total tracked memory count can be lower than the used heap size; No idea where Its missing objects or calculating incorrectly. Seems to miss about 10-30%.
Various Types throw errors due to having bad implementations for GetHashCode() or Equals (Object). Parsing of the root Type causing the error is currently stopped, and parsing of the next Type is started. (This may actually be the cause for the missing size)
