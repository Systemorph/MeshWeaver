using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;

namespace MeshWeaver.Blazor
{
    public class ModelParameter
    {
        public ModelParameter(JsonNode jsonObject)
        {
            Element = JsonDocument.Parse(jsonObject.ToJsonString()).RootElement;
            LastSubmitted = Element;
        }

        public JsonElement Element { get; set; }

        private readonly Queue<JsonElement> toBePersisted = new(); 
        private JsonElement LastSubmitted { get; set; }

        public void Update(JsonPatch patch)
        {
            Element = patch.Apply(Element);
        }

        public JsonElement Submit()
        {
            toBePersisted.Enqueue(Element);
            LastSubmitted = Element;
            return Element;
        }

        public bool HasChanges => !Element.Equals(LastSubmitted);

        public void Confirm()
        {
            toBePersisted.Dequeue();
        }

        public void Reset()
        {
            if(toBePersisted.TryDequeue(out var state))
                Element = state;
        }

    }
}
