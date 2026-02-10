import type { ReactNode } from "react";
import TatakiAucklandUnlimitedLogo from '../../assets/tataki-auckland-unlimited-a-navy-rgb.26dbb9e1deac145cabbb.svg?react';

interface TatakiLogoProps {
  width?: number | string;
  height?: number | string;
  className?: string;
}

export function TatakiLogo({
  width = 24,
  height = undefined,
  className,
}: TatakiLogoProps): ReactNode {
  return <TatakiAucklandUnlimitedLogo width={width} height={height} className={className} />;
}