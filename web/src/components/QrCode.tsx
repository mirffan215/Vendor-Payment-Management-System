import qrcode from 'qrcode-generator'
import { useMemo } from 'react'

/** Renders an otpauth:// URI as an SVG QR code, locally (the secret never leaves the browser). */
export function QrCode({ value, label }: { value: string; label: string }) {
  const svg = useMemo(() => {
    const qr = qrcode(0, 'M')
    qr.addData(value)
    qr.make()
    return qr.createSvgTag({ cellSize: 4, margin: 4, scalable: true })
  }, [value])
  return <div className="qr" role="img" aria-label={label} dangerouslySetInnerHTML={{ __html: svg }} />
}
