import Tooltip from "rc-tooltip";
import { ReactNode, useEffect, useRef } from "react";
import raf from 'rc-util/lib/raf';
import { TooltipProps } from "rc-tooltip/lib/Tooltip";
import { identity } from "lodash";
import "@open-smc/ui-kit/components/tooltip.scss";

interface HandleTooltipProps {
    value: number;
    visible: boolean;
    tipFormatter?: (value: number) => ReactNode;
    children?: TooltipProps['children'];
}

export function HandleTooltip({value, visible, tipFormatter = identity, children, ...props}: HandleTooltipProps) {
    const tooltipRef = useRef<any>();
    const rafRef = useRef<number | null>(null);

    function cancelKeepAlign() {
        raf.cancel(rafRef.current!);
    }

    function keepAlign() {
        rafRef.current = raf(() => {
            tooltipRef.current?.forcePopupAlign();
        });
    }

    useEffect(() => {
        if (visible) {
            keepAlign();
        } else {
            cancelKeepAlign();
        }

        return cancelKeepAlign;
    }, [value, visible]);

    return (
        <Tooltip
            placement={'bottom'}
            overlay={tipFormatter(value)}
            overlayInnerStyle={{minHeight: 'auto'}}
            ref={tooltipRef}
            overlayClassName={'blueTooltip'}
            visible={visible}
            {...props}
        >
            {children}
        </Tooltip>
    );
}