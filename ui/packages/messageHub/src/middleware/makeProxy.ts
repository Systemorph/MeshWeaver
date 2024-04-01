import { Subject } from "rxjs";
import { SubjectHub } from "../SubjectHub";
import { MessageDelivery } from "../api/MessageDelivery";

// export function makeProxy() {
//     const subject1 = new Subject<MessageDelivery>();
//     const subject2 = new Subject<MessageDelivery>();
//
//     const hub1 = new SubjectHub(subject1, subject2);
//     const hub2 = new SubjectHub(subject2, subject1);
//
//     return [hub1, hub2];
// }