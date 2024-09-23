using System.Text.Json;
using System.Text.Json.Nodes;
using Autofac.Core.Lifetime;
using Json.Patch;

namespace MeshWeaver.Blazor
{
    public class ModelParameter
    {
        public event EventHandler<JsonElement> ElementChanged; 
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
            if (ElementChanged != null)
            {
                ElementChanged(this, Element);
            }
        }

        public JsonElement Submit()
        {
            toBePersisted.Enqueue(LastSubmitted);
            LastSubmitted = Element;
            return Element;
        }

        public bool IsUpToDate => Element.Equals(LastSubmitted);

        public void Confirm()
        {
            toBePersisted.Dequeue();
        }

        public void Reset()
        {
           Element = LastSubmitted;
           if (toBePersisted.TryDequeue(out var lastPersisted))
               LastSubmitted = lastPersisted;
        }

    }
}
