import React, { Component, ErrorInfo, PropsWithChildren, ReactNode } from "react";

type Props = PropsWithChildren<{ fallback?: (error: unknown, reset: () => void) => ReactNode }>

type State = {
    hasError: boolean;
    error?: unknown;
}

export class ErrorBoundary extends Component<Props, State> {
    state: State = {
        hasError: false,
        error: null
    };

    static getDerivedStateFromError(error: Error) {
        return { hasError: true, error };
    }

    componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    }

    reset = () => this.setState({hasError: false, error: null})

    render() {
        const {hasError, error} = this.state;
        const {fallback, children} = this.props;

        return hasError
            ? (fallback && fallback(error, this.reset)) ?? <div style={{color: 'red'}}>Error</div>
            : children;
    }
}