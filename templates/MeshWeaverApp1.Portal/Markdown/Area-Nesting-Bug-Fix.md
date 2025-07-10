# Area Nesting Bug Fix

## Issue Description

The MeshWeaver layout system had a critical bug where layout areas were being created with double-prefixed names, causing them to appear at the wrong hierarchy level in the JSON structure.

**Before the fix:**
- Areas would appear as `"TodoList/TodoList/Item1"`, `"TodoList/TodoList/Item2"` 
- This caused areas to be created outside the intended parent area instead of being properly nested

**After the fix:**
- Areas correctly appear as `"TodoList/Item1"`, `"TodoList/Item2"`
- Areas are properly nested within their parent container

## Root Cause

The bug was in `ContainerControl.cs` in the renderer lambdas. The lambdas were trying to find areas in `This.Areas`, but `This` referred to the original container instance before the area was added, causing a runtime exception when trying to find an area that didn't exist yet.

```csharp
// OLD (buggy) code:
var matchingArea = This.Areas.First(a => a.Id == area.Id);  // This would fail!
```

The lambda closure captured the original container state, not the new state with the added area.

## Solution

The key insight was that the renderer lambdas were trying to access areas that hadn't been added to the container yet. The lambda closure captured `This.Areas`, which referred to the original container before the new area was added.

The fix was to change the renderer lambdas to use `GetContextForArea(context, area.Id.ToString())` instead of trying to look up the area from `This.Areas`:

```csharp
// NEW (fixed) code:
Renderers = Renderers.Add((host, context, store) => {
    var areaContext = GetContextForArea(context, area.Id.ToString());
    return host.RenderArea(areaContext, view, store);
})
```

This approach:
1. Uses the original area ID (not the full path)
2. Lets `GetContextForArea` properly construct the nested path
3. Avoids the closure issue with `This.Areas`
4. Keeps the `PrepareRendering` method to set area paths for other parts of the system

## Files Modified

1. **`src/MeshWeaver.Layout/ContainerControl.cs`**
   - Fixed 5 instances of renderer lambdas that were causing runtime exceptions
   - Changed from trying to look up areas in `This.Areas` to using `GetContextForArea(context, area.Id.ToString())`
   - Lines updated: ~81, ~108, ~127, ~146, ~174

2. **`test/MeshWeaver.Layout.Test/ContainerControlAreaNestingTest.cs`** (New)
   - Added comprehensive tests to verify the fix works correctly
   - Tests cover area structure, hierarchy, and rendering context behavior

## Impact

This fix ensures that:
- Layout areas are properly nested within their parent containers
- The JSON structure reflects the correct hierarchy
- Interactive features like TodoList work as expected
- No breaking changes to the public API

## Testing

Created unit tests that verify:
- ✅ Container controls have correct area structure
- ✅ Nested container hierarchies work properly
- ✅ RenderingContext operates correctly
- ✅ No double-prefixing occurs in area names

The fix has been validated with:
- Unit tests pass
- Full solution builds successfully
- No breaking changes detected

## Technical Details

The core issue was a closure problem in the renderer lambdas:
- **Problem**: Lambda captured `This.Areas` which referred to the original container before the area was added
- **Symptom**: Runtime exception when trying to find an area that didn't exist in the captured state
- **Solution**: Use `GetContextForArea(context, area.Id.ToString())` which doesn't depend on the container state

The fix maintains the existing `PrepareRendering` behavior for setting area paths, which other parts of the system depend on, while avoiding the closure issue in the renderer lambdas.

## Before and After Comparison

### Before Fix
```json
{
  "TodoList/TodoList/Item1": { "type": "MenuItemControl", "title": "First Todo" },
  "TodoList/TodoList/Item2": { "type": "MenuItemControl", "title": "Second Todo" }
}
```

### After Fix
```json
{
  "TodoList": {
    "areas": [
      { "area": "Item1", "type": "MenuItemControl", "title": "First Todo" },
      { "area": "Item2", "type": "MenuItemControl", "title": "Second Todo" }
    ]
  }
}
```

This demonstrates how the fix ensures proper nesting and eliminates the double-prefixing issue that was causing layout hierarchy problems.
