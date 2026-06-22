export function formatMoney(amount: number): string {
  return `NT$ ${amount.toLocaleString()}`
}
