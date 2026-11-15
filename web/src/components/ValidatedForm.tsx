import { Alert, FormControl, FormHelperText, Stack, TextField, type FormControlProps, type StackProps, type TextFieldProps } from '@mui/material';
import { createContext, useContext, useRef, useState } from 'react';
import type { ZodTypeAny } from 'zod';
import { fieldErrors } from '../shared/validation';

const ErrorsContext = createContext<Record<string, string>>({});
const BusyContext = createContext(false);

/** All submit paths (including Enter) pass through the same schema validation. */
export function ValidatedForm({ schema, value, onValid, busy, children, ...props }: Omit<StackProps<'form'>, 'onSubmit' | 'component'> & {
  schema: ZodTypeAny;
  value: unknown;
  onValid: () => void | Promise<unknown>;
  busy?: boolean;
}) {
  const [submitted, setSubmitted] = useState(false);
  const pending = useRef(false);
  const [nativeErrors, setNativeErrors] = useState<Record<string, string>>({});
  const errors = { ...(submitted ? fieldErrors(schema.safeParse(value)) : {}), ...nativeErrors };
  return <ErrorsContext.Provider value={errors}><BusyContext.Provider value={!!busy}>
    <Stack {...props} component="form" noValidate onChangeCapture={() => setNativeErrors({})} onSubmit={async event => {
      event.preventDefault();
      if (busy || pending.current) return;
      setSubmitted(true);
      const invalidInputs = Array.from(event.currentTarget.elements).filter((element): element is HTMLInputElement => element instanceof HTMLInputElement && element.validity.badInput);
      setNativeErrors(Object.fromEntries(invalidInputs.map(input => [input.name, 'Введите корректное значение'])));
      if (invalidInputs.length === 0 && schema.safeParse(value).success) {
        pending.current = true;
        try { await onValid(); }
        catch { /* The mutation's error state is displayed by the owning form. */ }
        finally { pending.current = false; }
      }
    }}>
      {Object.keys(errors).length > 0 && <Alert severity="error">Исправьте ошибки в полях формы.</Alert>}
      {children}
    </Stack>
  </BusyContext.Provider></ErrorsContext.Provider>;
}

export function ValidatedTextField(props: TextFieldProps) {
  const errors = useContext(ErrorsContext);
  const busy = useContext(BusyContext);
  const message = props.name ? errors[props.name] : undefined;
  return <TextField {...props} disabled={busy || props.disabled} error={!!message || props.error} helperText={message ?? props.helperText} />;
}

export function ValidatedControl({ name, children, ...props }: FormControlProps & { name?: string }) {
  const errors = useContext(ErrorsContext);
  const busy = useContext(BusyContext);
  const message = name ? errors[name] : undefined;
  return <FormControl {...props} disabled={busy || props.disabled} error={!!message || props.error}>
    {children}
    {message && <FormHelperText>{message}</FormHelperText>}
  </FormControl>;
}
