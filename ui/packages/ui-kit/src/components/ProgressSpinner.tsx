import * as React from "react";
import classNames from "classnames";
import "./progress.scss";

export interface SpinnerProps {
    id?: string;
    style?: React.CSSProperties;
    className?: string;
    strokeWidth?: string;
    fill?: string;
    animationDuration?: string;
    ref?: React.Ref<HTMLDivElement>;
    children?: undefined;
}

export const ProgressSpinner = React.memo(
    React.forwardRef((props: SpinnerProps, ref) => {
        const {
            className,
            id,
            style,
            animationDuration,
            fill,
            strokeWidth,
            ...otherProps
        } = props;
        const defaultOptions = {
            animationDuration: "2s",
            fill: "none",
            strokeWidth: "2",
        };
        const elementRef = React.useRef(null);
        const newClassName = classNames("progress-spinner", className);

        React.useImperativeHandle(ref, () => ({
            props,
            getElement: () => elementRef.current,
        }));

        return (
            <div
                id={id}
                ref={elementRef}
                style={style}
                className={newClassName}
                role="alert"
                aria-busy
                {...otherProps}
            >
                <svg
                    className="progress-spinner-svg"
                    viewBox="25 25 50 50"
                    style={{
                        animationDuration: animationDuration
                            ? animationDuration
                            : defaultOptions.animationDuration,
                    }}
                >
                    <circle
                        className="progress-spinner-circle"
                        cx="50"
                        cy="50"
                        r="20"
                        fill={fill ? fill : defaultOptions.fill}
                        strokeWidth={
                            strokeWidth
                                ? strokeWidth
                                : defaultOptions.strokeWidth
                        }
                        strokeMiterlimit="10"
                    />
                </svg>
            </div>
        );
    })
);
