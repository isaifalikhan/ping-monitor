import { useState } from 'react';
import { Network } from 'lucide-react';
import { LOGO_URL, ORG_NAME, PRODUCT_NAME } from '../brand';

/** Logo + product name. Falls back to a neutral icon if the logo file is missing. */
export function BrandMark({ size = 22, suffix }: { size?: number; suffix?: string }) {
  const [logoOk, setLogoOk] = useState(true);
  return (
    <div className="brand" title={`${PRODUCT_NAME} — ${ORG_NAME}`}>
      {logoOk ? (
        <img className="brand-logo" src={LOGO_URL} alt={ORG_NAME} width={size} height={size} onError={() => setLogoOk(false)} />
      ) : (
        <Network size={size} />
      )}
      <span>
        {PRODUCT_NAME}
        {suffix ? ` ${suffix}` : ''}
      </span>
    </div>
  );
}
