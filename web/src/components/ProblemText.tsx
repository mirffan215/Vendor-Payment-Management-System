import { useProblemText } from '../hooks/useProblemText'

export function ProblemAlert({ error }: { error: unknown }) {
  const text = useProblemText()
  if (!error) return null
  return <p role="alert" className="alert">{text(error)}</p>
}
