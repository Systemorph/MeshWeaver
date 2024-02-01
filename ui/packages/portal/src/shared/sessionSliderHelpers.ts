import { round } from "lodash";
import { SessionTierSpecificationDto } from "../features/project/projectSessionApi";

export const CPU_STEP = 0.5;

export const getMemoryStep = (tier: SessionTierSpecificationDto) => {
    return CPU_STEP * (tier.maxMemory - tier.minMemory) / (tier.maxCpu - tier.minCpu);
}

export const calculateMemory = (cpu: number, tier: SessionTierSpecificationDto) => {
    const memoryStep = getMemoryStep(tier);
    return (cpu - tier.minCpu) * memoryStep / CPU_STEP + tier.minMemory;
}

export const calculateCpu = (memory: number, tier: SessionTierSpecificationDto) => {
    const memoryStep = getMemoryStep(tier);
    return (memory - tier.minMemory) * CPU_STEP / memoryStep + tier.minCpu;
}

export const toMarks = (arr: number[]) => arr.reduce((acc, item) => {
    acc[item] = round(item, 1);
    return acc
}, {} as Record<string | number, number>);