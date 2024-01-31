import EventEmitter from "event-emitter";

const eventEmitter = EventEmitter();

const eventName = 'contentWidthChanged';

export const subscribeToContentWidthChanged = (handler: () => void) => {
    eventEmitter.on(eventName, handler);
    return () => eventEmitter.off(eventName, handler);
}

export const triggerContentWidthChanged = () => eventEmitter.emit(eventName);