import * as React from "react"
import { Check, ChevronDown } from "lucide-react"
import { Select as SelectPrimitive } from "radix-ui"
import { cn } from "cn"
import { cspNonce } from "@/lib/csp"

/**
 * Drop-down of the portal (0.9.4). Written like a <select> with <option> children and used the same way
 * (value, onChange with e.target.value, id for the label, disabled), but the open list is drawn by the portal
 * (Radix Select) instead of the operating system: same look in every browser, light and dark, with the chosen entry
 * marked, keyboard and type-ahead as in a native list. The name is kept from the native control it replaced.
 */
type ChangeLike = { target: { value: string }; currentTarget: { value: string } }

interface NativeSelectProps {
  value?: string | number | null
  onChange?: (e: ChangeLike) => void
  id?: string
  disabled?: boolean
  className?: string
  children?: React.ReactNode
  "aria-label"?: string
  "aria-invalid"?: boolean | "true" | "false"
  "aria-describedby"?: string
}

interface Entry { value: string; label: React.ReactNode; disabled: boolean; group?: string }

// Radix does not accept an empty value for an item: "" (the usual "none" option) travels under this name inside the list.
const EMPTY = "\u0000none"
const inside = (v: string) => (v === "" ? EMPTY : v)
const outside = (v: string) => (v === EMPTY ? "" : v)

function text(node: React.ReactNode): string {
  if (node == null || typeof node === "boolean") return ""
  if (typeof node === "string" || typeof node === "number") return String(node)
  if (Array.isArray(node)) return node.map(text).join("")
  if (React.isValidElement<{ children?: React.ReactNode }>(node)) return text(node.props.children)
  return ""
}

/** The <option> (and <optgroup>) children as a flat list, through fragments and arrays. */
function entries(children: React.ReactNode, group?: string, out: Entry[] = []): Entry[] {
  React.Children.forEach(children, (child) => {
    if (!React.isValidElement<{ children?: React.ReactNode; value?: string | number; disabled?: boolean; label?: string }>(child)) return
    if (child.type === React.Fragment) {
      entries(child.props.children, group, out)
    } else if (child.type === "optgroup") {
      entries(child.props.children, child.props.label ?? "", out)
    } else if (child.type === "option") {
      out.push({ value: String(child.props.value ?? text(child.props.children)), label: child.props.children, disabled: child.props.disabled === true, group })
    }
  })
  return out
}

function NativeSelect({ value, onChange, id, disabled, className, children, ...aria }: NativeSelectProps) {
  const list = entries(children)
  const current = value == null ? "" : String(value)
  const known = list.some((e) => e.value === current)
  const groups = [...new Set(list.map((e) => e.group))]
  return (
    <SelectPrimitive.Root value={known ? inside(current) : ""} disabled={disabled}
      onValueChange={(v) => { const next = outside(v); onChange?.({ target: { value: next }, currentTarget: { value: next } }) }}>
      <SelectPrimitive.Trigger id={id} data-slot="native-select" {...aria}
        className={cn(
          "group/select flex h-8 w-full min-w-0 cursor-pointer items-center justify-between gap-2 rounded-lg border border-input bg-background py-1 pr-2.5 pl-2.5 text-left text-sm shadow-xs outline-none transition-[color,background-color,border-color,box-shadow] select-none",
          "hover:border-ring/60 hover:bg-accent/40 focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 data-[state=open]:border-ring data-[state=open]:ring-3 data-[state=open]:ring-ring/30",
          "disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:border-input disabled:hover:bg-background aria-invalid:border-destructive data-placeholder:text-muted-foreground dark:bg-input/30 dark:hover:bg-input/50",
          className,
        )}>
        <span className="min-w-0 flex-1 truncate"><SelectPrimitive.Value placeholder="—" /></span>
        <SelectPrimitive.Icon asChild>
          <ChevronDown className="size-4 shrink-0 text-muted-foreground transition-transform duration-150 group-hover/select:text-foreground group-data-[state=open]/select:rotate-180" aria-hidden="true" />
        </SelectPrimitive.Icon>
      </SelectPrimitive.Trigger>
      <SelectPrimitive.Portal>
        <SelectPrimitive.Content position="popper" side="bottom" align="start" sideOffset={4} collisionPadding={8} data-slot="native-select-list"
          className="relative z-50 max-h-[min(20rem,var(--radix-select-content-available-height))] w-(--radix-select-trigger-width) min-w-40 max-w-[calc(100vw-1rem)] origin-(--radix-select-content-transform-origin) overflow-hidden rounded-lg border bg-popover text-popover-foreground shadow-lg duration-100 data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-95 data-[side=bottom]:slide-in-from-top-1 data-[side=top]:slide-in-from-bottom-1 data-[state=closed]:animate-out data-[state=closed]:fade-out-0">
          <SelectPrimitive.Viewport nonce={cspNonce} className="max-h-[inherit] p-1">
            {groups.map((g, gi) => {
              const items = list.filter((e) => e.group === g).map((e) => (
                <SelectPrimitive.Item key={e.value} value={inside(e.value)} disabled={e.disabled}
                  className="relative flex w-full cursor-pointer items-start gap-2 rounded-md py-1.5 pr-8 pl-2 text-sm outline-none select-none data-highlighted:bg-accent data-highlighted:text-accent-foreground data-[state=checked]:font-medium data-disabled:cursor-not-allowed data-disabled:text-muted-foreground data-disabled:opacity-60">
                  <span className="min-w-0 flex-1 [overflow-wrap:anywhere]"><SelectPrimitive.ItemText>{e.label}</SelectPrimitive.ItemText></span>
                  <span className="absolute top-2 right-2 flex size-4 items-center justify-center text-primary">
                    <SelectPrimitive.ItemIndicator><Check className="size-4" /></SelectPrimitive.ItemIndicator>
                  </span>
                </SelectPrimitive.Item>
              ))
              return g === undefined ? <React.Fragment key={gi}>{items}</React.Fragment> : (
                <SelectPrimitive.Group key={gi} className={cn(gi > 0 && "mt-1 border-t pt-1")}>
                  <SelectPrimitive.Label className="px-2 py-1 text-xs font-semibold text-muted-foreground">{g}</SelectPrimitive.Label>
                  {items}
                </SelectPrimitive.Group>
              )
            })}
          </SelectPrimitive.Viewport>
        </SelectPrimitive.Content>
      </SelectPrimitive.Portal>
    </SelectPrimitive.Root>
  )
}

export { NativeSelect }
