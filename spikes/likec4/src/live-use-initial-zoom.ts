import {useLayoutEffect, useState} from 'react';
import type {RefObject} from 'react';

/** Prefer readable labels initially; the native Fit control remains unrestricted. */
export function readableInitialZoom(width:number, height:number, bounds:{width:number; height:number}):number {
  const fit = Math.min(Math.max(1, width - 48) / Math.max(1, bounds.width), Math.max(1, height - 48) / Math.max(1, bounds.height));
  return Math.max(0.5, Math.min(1, fit));
}

export function useLiveInitialZoom(container:RefObject<HTMLElement|null>, bounds:{width:number; height:number}) {
  const [size, setSize] = useState<{width:number; height:number}|null>(null);
  useLayoutEffect(() => {
    const element = container.current;
    if (!element) return;
    const measure = () => {
      const width = element.clientWidth, height = element.clientHeight;
      if (width > 0 && height > 0) setSize(previous => previous?.width === width && previous.height === height ? previous : {width, height});
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [container]);
  return {ready:size !== null, zoom:size ? readableInitialZoom(size.width, size.height, bounds) : 0.5};
}
