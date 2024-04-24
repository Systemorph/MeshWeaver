var __defProp = Object.defineProperty;
var __defNormalProp = (obj, key, value) => key in obj ? __defProp(obj, key, { enumerable: true, configurable: true, writable: true, value }) : obj[key] = value;
var __publicField = (obj, key, value) => {
  __defNormalProp(obj, typeof key !== "symbol" ? key + "" : key, value);
  return value;
};
import { Subscription, Observable, filter, mergeMap, from, map, Subject, of } from "rxjs";
import { methodName } from "./contract.mjs";
import { immerable, enablePatches, produceWithPatches } from "immer";
import { cloneDeepWith, assign, mapValues, isFunction } from "lodash-es";
Subscription.prototype.toJSON = (key) => {
  return {};
};
class WebSocketClientHub extends Observable {
  constructor(webSocketClient, webSocket) {
    super((subscriber) => {
      const handler = (data, client) => client === webSocketClient && subscriber.next(data);
      webSocket.on(methodName, handler);
      subscriber.add(() => webSocket.off(methodName, handler));
    });
    this.webSocketClient = webSocketClient;
  }
  complete() {
  }
  error(err) {
  }
  next(value) {
    this.webSocketClient.send(methodName, value);
  }
}
function connect(hub1, hub2) {
  const subscription = hub1.subscribe(hub2);
  subscription.add(hub2.subscribe(hub1));
  return subscription;
}
const typeRegistry = /* @__PURE__ */ new Map();
const getConstructor = (type2) => typeRegistry.get(type2);
function type(typeName) {
  return function(constructor) {
    typeRegistry.set(typeName, constructor);
    constructor.$type = typeName;
    constructor[immerable] = true;
    return constructor;
  };
}
class Request {
  constructor(responseType) {
    this.responseType = responseType;
  }
}
var __defProp$5 = Object.defineProperty;
var __getOwnPropDesc$5 = Object.getOwnPropertyDescriptor;
var __decorateClass$5 = (decorators, target, key, kind) => {
  var result = kind > 1 ? void 0 : kind ? __getOwnPropDesc$5(target, key) : target;
  for (var i = decorators.length - 1, decorator; i >= 0; i--)
    if (decorator = decorators[i])
      result = (kind ? decorator(target, key, result) : decorator(result)) || result;
  if (kind && result)
    __defProp$5(target, key, result);
  return result;
};
let DataChangedEvent = class {
  constructor(reference, change, changeType) {
    this.reference = reference;
    this.change = change;
    this.changeType = changeType;
  }
  // // keeping change raw since the patch is meant to be applied to the json store as-is
  // static deserialize(props: DataChangedEvent) {
  //     const {reference, change, changeType} = props;
  //     return new DataChangedEvent(deserialize(reference), change, changeType);
  // }
};
DataChangedEvent = __decorateClass$5([
  type("OpenSmc.Data.DataChangedEvent")
], DataChangedEvent);
var __defProp$4 = Object.defineProperty;
var __getOwnPropDesc$4 = Object.getOwnPropertyDescriptor;
var __decorateClass$4 = (decorators, target, key, kind) => {
  var result = kind > 1 ? void 0 : kind ? __getOwnPropDesc$4(target, key) : target;
  for (var i = decorators.length - 1, decorator; i >= 0; i--)
    if (decorator = decorators[i])
      result = (kind ? decorator(target, key, result) : decorator(result)) || result;
  if (kind && result)
    __defProp$4(target, key, result);
  return result;
};
let SubscribeRequest = class extends Request {
  constructor(reference) {
    super(DataChangedEvent);
    this.reference = reference;
  }
};
SubscribeRequest = __decorateClass$4([
  type("OpenSmc.Data.SubscribeRequest")
], SubscribeRequest);
var __defProp$3 = Object.defineProperty;
var __getOwnPropDesc$3 = Object.getOwnPropertyDescriptor;
var __decorateClass$3 = (decorators, target, key, kind) => {
  var result = kind > 1 ? void 0 : kind ? __getOwnPropDesc$3(target, key) : target;
  for (var i = decorators.length - 1, decorator; i >= 0; i--)
    if (decorator = decorators[i])
      result = (kind ? decorator(target, key, result) : decorator(result)) || result;
  if (kind && result)
    __defProp$3(target, key, result);
  return result;
};
let JsonPatch = class {
  constructor(operations) {
    this.operations = operations;
  }
  // serialize() {
  //     return {...this};
  // }
  //
  // // keep raw operation values
  // static deserialize(props: JsonPatch) {
  //     const {operations} = props;
  //     return new JsonPatch(operations);
  // }
};
JsonPatch = __decorateClass$3([
  type("Json.Patch.JsonPatch")
], JsonPatch);
class WorkspaceReference {
}
var __defProp$2 = Object.defineProperty;
var __getOwnPropDesc$2 = Object.getOwnPropertyDescriptor;
var __decorateClass$2 = (decorators, target, key, kind) => {
  var result = kind > 1 ? void 0 : kind ? __getOwnPropDesc$2(target, key) : target;
  for (var i = decorators.length - 1, decorator; i >= 0; i--)
    if (decorator = decorators[i])
      result = (kind ? decorator(target, key, result) : decorator(result)) || result;
  if (kind && result)
    __defProp$2(target, key, result);
  return result;
};
let LayoutAreaReference = class extends WorkspaceReference {
  constructor(area) {
    super();
    this.area = area;
  }
};
LayoutAreaReference = __decorateClass$2([
  type("OpenSmc.Data.LayoutAreaReference")
], LayoutAreaReference);
var __defProp$1 = Object.defineProperty;
var __getOwnPropDesc$1 = Object.getOwnPropertyDescriptor;
var __decorateClass$1 = (decorators, target, key, kind) => {
  var result = kind > 1 ? void 0 : kind ? __getOwnPropDesc$1(target, key) : target;
  for (var i = decorators.length - 1, decorator; i >= 0; i--)
    if (decorator = decorators[i])
      result = (kind ? decorator(target, key, result) : decorator(result)) || result;
  if (kind && result)
    __defProp$1(target, key, result);
  return result;
};
let DataChangeResponse = class {
  constructor(status) {
    this.status = status;
  }
};
DataChangeResponse = __decorateClass$1([
  type("OpenSmc.Data.DataChangeResponse")
], DataChangeResponse);
class DataChangeRequest extends Request {
  constructor() {
    super(DataChangeResponse);
  }
}
var __defProp2 = Object.defineProperty;
var __getOwnPropDesc = Object.getOwnPropertyDescriptor;
var __decorateClass = (decorators, target, key, kind) => {
  var result = kind > 1 ? void 0 : kind ? __getOwnPropDesc(target, key) : target;
  for (var i = decorators.length - 1, decorator; i >= 0; i--)
    if (decorator = decorators[i])
      result = (kind ? decorator(target, key, result) : decorator(result)) || result;
  if (kind && result)
    __defProp2(target, key, result);
  return result;
};
let PatchChangeRequest = class extends DataChangeRequest {
  constructor(address, reference, change) {
    super();
    this.address = address;
    this.reference = reference;
    this.change = change;
  }
};
PatchChangeRequest = __decorateClass([
  type("OpenSmc.Data.PatchChangeRequest")
], PatchChangeRequest);
const messageOfType = (ctor) => (envelope) => envelope.message instanceof ctor;
const pack = (envelope) => (message) => ({ ...envelope, message });
const handleRequest = (requestType, handler) => (source) => source.pipe(filter(messageOfType(requestType))).pipe(
  mergeMap(
    ({ id, message }) => from(handler(message)).pipe(
      map(
        pack({
          properties: {
            requestId: id
          }
        })
      )
    )
  )
);
function sendMessage(observer, message, envelope) {
  observer.next({
    ...envelope ?? {},
    message
  });
}
function toPatchOperation(patch) {
  const { op, path, value } = patch;
  return {
    op,
    path: "/" + path.join("/"),
    value
  };
}
const basicStoreExample = {
  $type: "OpenSmc.Data.EntityStore",
  reference: {
    $type: "OpenSmc.Data.LayoutAreaReference",
    area: "MainWindow"
  },
  collections: {
    "OpenSmc.Layout.UiControl": {
      MainWindow: {
        $type: "OpenSmc.Layout.Composition.LayoutStackControl",
        skin: "MainWindow",
        areas: [
          {
            $type: "OpenSmc.Data.EntityReference",
            collection: "OpenSmc.Layout.UiControl",
            id: "Main"
          },
          {
            $type: "OpenSmc.Data.EntityReference",
            collection: "OpenSmc.Layout.UiControl",
            id: "Toolbar"
          },
          {
            $type: "OpenSmc.Data.EntityReference",
            collection: "OpenSmc.Layout.UiControl",
            id: "ContextMenu"
          }
        ]
      },
      "Main": {
        $type: "OpenSmc.Layout.Composition.SpinnerControl",
        message: "processing...",
        progress: 0.5
      },
      "Toolbar": {
        $type: "OpenSmc.Layout.TextBoxControl",
        dataContext: {
          $type: "OpenSmc.Data.EntityReference",
          collection: "LineOfBusiness",
          id: "1"
        },
        data: {
          $type: "OpenSmc.Layout.DataBinding.Binding",
          path: "$.DisplayName"
        }
      },
      "ContextMenu": {
        $type: "OpenSmc.Layout.Views.MenuItemControl",
        dataContext: {
          $type: "OpenSmc.Data.JsonPathReference",
          path: "$.LineOfBusiness.1"
        },
        title: {
          $type: "OpenSmc.Layout.DataBinding.Binding",
          path: "$.DisplayName"
        },
        icon: "systemorph-fill"
      }
    },
    LineOfBusiness: {
      "1": {
        SystemName: "1",
        DisplayName: "1"
      },
      "2": {
        SystemName: "2",
        DisplayName: "2"
      }
    }
  }
};
enablePatches();
class SampleApp extends Observable {
  constructor() {
    super(
      (subscriber) => this.output.subscribe(subscriber)
    );
    __publicField(this, "input", new Subject());
    __publicField(this, "output", new Subject());
    __publicField(this, "subscribeRequestHandler", () => (message) => {
      const { reference } = message;
      if (reference instanceof LayoutAreaReference) {
        setTimeout(() => {
          const [nextState, patches] = produceWithPatches(
            basicStoreExample,
            (state) => {
              state.collections.LineOfBusiness["1"].DisplayName = "Hello";
            }
          );
          sendMessage(
            this.output,
            new DataChangedEvent(reference, new JsonPatch(patches.map(toPatchOperation)), "Patch")
          );
        }, 1e3);
        return of(new DataChangedEvent(reference, basicStoreExample, "Full"));
      }
      throw "Reference type not supported";
    });
    __publicField(this, "patchChangeRequestHandler", () => (message) => {
      console.log(message);
      return of(new DataChangeResponse("Committed"));
    });
    this.input.pipe(handleRequest(SubscribeRequest, this.subscribeRequestHandler())).subscribe(this.output);
    this.input.pipe(handleRequest(PatchChangeRequest, this.patchChangeRequestHandler())).subscribe(this.output);
  }
  complete() {
  }
  error(err) {
  }
  next(value) {
    this.input.next(value);
  }
}
class SamplesServer extends Observable {
  constructor() {
    super(
      (subscriber) => this.app.subscribe(subscriber)
    );
    __publicField(this, "app", new SampleApp());
  }
  complete() {
  }
  error(err) {
  }
  next(value) {
    this.app.next(value);
  }
}
const isDeserializable = (value) => (value == null ? void 0 : value.$type) !== void 0;
const deserialize = (value) => {
  return cloneDeepWith(
    value,
    (value2) => {
      if (isDeserializable(value2)) {
        const { $type, ...props } = value2;
        const constructor = getConstructor($type);
        if (constructor) {
          if (constructor.deserialize) {
            return constructor.deserialize(props);
          }
          return assign(new constructor(), plainObjectDeserializer(props));
        }
        return plainObjectDeserializer(props);
      }
    }
  );
};
const plainObjectDeserializer = (props) => mapValues(props, deserialize);
const isSerializable = (value) => isFunction(value == null ? void 0 : value.constructor) && value.constructor.$type !== void 0;
const serialize = (value) => {
  return cloneDeepWith(
    value,
    (value2) => {
      if (isSerializable(value2)) {
        const { $type } = value2.constructor;
        if (value2.serialize !== void 0) {
          return value2.serialize();
        }
        return {
          $type,
          ...mapValues(value2, serialize)
        };
      }
    }
  );
};
class SerializationMiddleware extends Observable {
  constructor(hub) {
    super(
      (subscriber) => hub.pipe(map(deserialize)).subscribe((value) => {
        setTimeout(() => subscriber.next(value));
      })
    );
    this.hub = hub;
  }
  complete() {
  }
  error(err) {
  }
  next(value) {
    setTimeout(() => this.hub.next(serialize(value)));
  }
}
function samplesServerPlugin() {
  return {
    name: "samplesServer",
    configureServer(server) {
      const { ws } = server;
      ws.on("connection", function(socket, request) {
        ws.clients.forEach((client) => {
          if (client.socket === socket) {
            const clientHub = new WebSocketClientHub(client, ws);
            connect(new SerializationMiddleware(clientHub), new SamplesServer());
          }
        });
      });
    }
  };
}
export {
  samplesServerPlugin
};
