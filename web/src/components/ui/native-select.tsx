import * as React from "react"
import { ChevronDown } from "lucide-react"
import { cn } from "cn"

/** Native <select> styled like the other inputs: accessible, keyboard friendly, no portal. */
function NativeSelect({ className, children, ...props }: React.ComponentProps<"select">) {
  return (
    <div className={cn("relative w-full", className)} data-slot="native-select-wrapper">
      <select
        data-slot="native-select"
        className="peer h-8 w-full min-w-0 cursor-pointer appearance-none truncate rounded-lg border border-input bg-background py-1 pr-8 pl-2.5 text-sm shadow-xs transition-[color,background-color,border-color,box-shadow] outline-none hover:border-ring/60 hover:bg-accent/40 focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:bg-background aria-invalid:border-destructive dark:bg-input/30 dark:hover:bg-input/50 [&_option]:bg-popover [&_option]:text-popover-foreground [&_option:disabled]:text-muted-foreground [&_optgroup]:bg-popover [&_optgroup]:font-semibold [&_optgroup]:text-muted-foreground"
        {...props}
      >
        {children}
      </select>
      <ChevronDown className="pointer-events-none absolute top-1/2 right-2.5 size-4 -translate-y-1/2 text-muted-foreground transition-colors peer-hover:text-foreground" aria-hidden="true" />
    </div>
  )
}

export { NativeSelect }
