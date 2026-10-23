// Builds a purchase order model with the typed visitor: the TypeScript version of the .NET example in
// `dotnet/docs/examples/PurchaseOrders`, on the same grammar. `purchase-order.lark.ts` is generated from the grammar:
//
//   npx etalii-lark generate purchase-order.lark --parser lalr --start order

import type { Token } from '../../src/index.js';
import * as PurchaseOrders from './purchase-order.lark.js';

// The model. Amounts are in cents, percentages in hundredths of a percent.

export type Currency = 'EUR' | 'USD' | 'GBP';
export type CustomerTier = 'bronze' | 'silver' | 'gold';
export type OrderField = 'order.total' | 'line.quantity' | 'customer.tier';
export type ComparisonOperator = '=' | '!=' | '<' | '<=' | '>' | '>=';

export interface PurchaseOrder {
  id: string;
  customer: Customer;
  currency: Currency;
  lines: OrderLine[];
  shipping: Shipping | null;
  rules: PricingRule[];
}

export interface Customer {
  name: string;
  tier: CustomerTier;
}

export interface OrderLine {
  sku: string;
  description: string;
  quantity: number;
  unitPrice: number;
  discount: number | null;
}

export type Shipping =
  | { kind: 'standard'; address: string[] }
  | { kind: 'express'; address: string[] }
  | { kind: 'pickup'; location: string };

export interface PricingRule {
  when: Condition;
  then: RuleAction;
}

export type Condition =
  | { kind: 'anyOf'; left: Condition; right: Condition }
  | { kind: 'allOf'; left: Condition; right: Condition }
  | { kind: 'comparison'; field: OrderField; op: ComparisonOperator; value: Operand };

export type Operand = { kind: 'amount'; amount: number } | { kind: 'count'; count: number } | { kind: 'tier'; tier: CustomerTier };

export type RuleAction = { kind: 'percentDiscount'; percentage: number } | { kind: 'freeShipping' } | { kind: 'requireApproval'; role: string };

export const lineTotal = (line: OrderLine): number => (line.quantity * line.unitPrice * (10_000 - (line.discount ?? 0))) / 10_000;

// The result type of each rule, alias and terminal. The handlers below get their children as these types.

type Results = {
  order: PurchaseOrder;
  customer: Customer;
  line: OrderLine;
  standard_shipping: Shipping;
  express_shipping: Shipping;
  pickup: Shipping;
  address: string[];
  rule: PricingRule;
  any_of: Condition;
  all_of: Condition;
  comparison: Condition;
  amount_operand: Operand;
  count_operand: Operand;
  tier_operand: Operand;
  percent_discount: RuleAction;
  free_shipping: RuleAction;
  require_approval: RuleAction;
  TEXT: string;
  COUNT: number;
  AMOUNT: number;
  PERCENT: number;
  TIER: CustomerTier;
  CURRENCY: Currency;
  FIELD: OrderField;
  OP: ComparisonOperator;
};

/** `1299.00` → 129900 cents, `12.5%` → 1250 hundredths of a percent. */
function hundredths(token: Token): number {
  const [whole, fraction = ''] = token.value.replace(/%$/, '').split('.');
  return Number(whole) * 100 + Number(fraction.padEnd(2, '0').slice(0, 2));
}

const build = PurchaseOrders.visitor<Results>({
  order: (_, id, customer, currency, lines, shipping, rules) => ({ id, customer, currency, lines, shipping, rules }),
  customer: (_, name, tier) => ({ name, tier: tier ?? 'bronze' }),
  line: (_, sku, description, quantity, unitPrice, discount) => {
    if (quantity === 0) throw new Error(`Order line ${sku} has no quantity.`);
    return { sku, description, quantity, unitPrice, discount };
  },
  standard_shipping: (_, address) => ({ kind: 'standard', address }),
  express_shipping: (_, address) => ({ kind: 'express', address }),
  pickup: (_, location) => ({ kind: 'pickup', location }),
  address: (_, lines) => lines,
  rule: (_, when, then) => ({ when, then }),
  any_of: (_, left, right) => ({ kind: 'anyOf', left, right }),
  all_of: (_, left, right) => ({ kind: 'allOf', left, right }),
  comparison: (_, field, op, value) => ({ kind: 'comparison', field, op, value }),
  amount_operand: (_, amount) => ({ kind: 'amount', amount }),
  count_operand: (_, count) => ({ kind: 'count', count }),
  tier_operand: (_, tier) => ({ kind: 'tier', tier }),
  percent_discount: (_, percentage) => ({ kind: 'percentDiscount', percentage }),
  free_shipping: () => ({ kind: 'freeShipping' }),
  require_approval: (_, role) => ({ kind: 'requireApproval', role }),

  // Terminals
  TEXT: (token) => JSON.parse(token.value) as string,
  COUNT: (token) => Number(token.value),
  AMOUNT: hundredths,
  PERCENT: hundredths,
  TIER: (token) => token.value as CustomerTier,
  CURRENCY: (token) => token.value as Currency,
  FIELD: (token) => token.value as OrderField,
  OP: (token) => token.value as ComparisonOperator,
});

/** Parses a purchase order. */
export function parsePurchaseOrder(text: string): PurchaseOrder {
  return build(PurchaseOrders.parse(text));
}

export const ORDER = `
order "PO-2026-0142"
customer "ACME Corp" (gold)
currency EUR
line "LT-14" "Laptop 14 inch" qty 5 at 1299.00
line "DS-02" "Docking station" qty 5 at 189.50 discount 10%
ship express to "Keizersgracht 1" "1015 CS Amsterdam" "NL"
rule when order.total > 5000.00 and customer.tier = gold then discount 5%
rule when line.quantity >= 50 or (order.total >= 20000.00) then require approval by "finance"
`;

/** Prints the order, as the Rust and .NET examples do. */
export function main(): string {
  const order = parsePurchaseOrder(ORDER);
  const total = order.lines.map(lineTotal).reduce((a, b) => a + b, 0);
  const cents = String(total % 100).padStart(2, '0');
  const currency = order.currency[0] + order.currency.slice(1).toLowerCase();
  return `${order.id} for ${order.customer.name}: ${order.lines.length} lines, total ${Math.floor(total / 100)}.${cents} ${currency}`;
}
