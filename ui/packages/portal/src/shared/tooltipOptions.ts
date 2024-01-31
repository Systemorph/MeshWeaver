import { TooltipProps } from "rc-tooltip/lib/Tooltip";

export const rcTooltipOptions: Omit<TooltipProps, "overlay"> = {
    placement: 'top',
    overlayClassName: "tooltip",
    mouseEnterDelay: 0.5,
    mouseLeaveDelay: 0.2
}